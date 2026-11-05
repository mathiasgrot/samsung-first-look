"""
MediaServerExt - state machine for the media_server COMP.

States
  IDLE    idle loop playing
  T_IN    transition in plays once  (transparent -> opaque)
  T_MID   transition mid loops      (fully opaque) - base layer is swapped on entry
  T_OUT   transition out plays once (opaque -> transparent)
  ACTIVE  content video a|b plays once

Flow
  IDLE --play a|b--> T_IN -> T_MID (loops until release) --release--> T_OUT -> ACTIVE
  ACTIVE --(video end - T_IN length)--> T_IN -> T_MID (loops until release) --release--> T_OUT -> IDLE
         T_IN starts early so the video plays under it and its last frame lands
         under T_IN's last (fully opaque) frame. Par Returntrim nudges this.
  ACTIVE --stop--> the same return, started right away (a stop during T_OUT on the way
         in is kept and starts the return as soon as ACTIVE is reached)
  Both ways: a release that arrives during T_IN skips T_MID (T_IN -> T_OUT).
  With no client connected nobody can release, so T_MID is skipped / ends after one cycle.
  any state --cancel--> IDLE (hard cut, no transition)

Protocol (JSON over WebSocket, TD is the host)
  -> {"cmd": "play", "video": "a"}   only accepted in IDLE
  -> {"cmd": "stop"}                 return to IDLE with a transition (ACTIVE, or T_OUT going in)
  -> {"cmd": "release"}              ends T_MID (accepted during T_IN or T_MID, both ways)
  -> {"cmd": "cancel"}               hard cut to IDLE
  -> {"cmd": "state"}                ask for current state
  -> {"cmd": "config", "values": {"debug": true, ...}}
                                     startup settings, sent by Unity on every connect.
                                     Each key sets the media_server custom par of the same
                                     name, capitalized (debug -> Debug, offsetx -> Offsetx)
  <- {"ok": true|false, "state": ..., "video": ..., "error"?: ...}   reply to sender
  <- {"event": "state", "state": ..., "video": ...}                  broadcast on every change

The network calls OnClipEnd(key) when a player shows its last frame.
T_MID is looped manually (cue back to 0 on its last frame) so T_OUT can start
exactly at the end of a cycle, where the frames match.
"""

import json
import TDFunctions as TDF

IDLE, T_IN, T_MID, T_OUT, ACTIVE = 'IDLE', 'T_IN', 'T_MID', 'T_OUT', 'ACTIVE'
TO_ACTIVE, TO_IDLE = 'to_active', 'to_idle'

VIDEOS = ('a', 'b')

# player key -> slot null inside this COMP.
# Wire a moviefileinTOP into each slot null (directly, or through ops on input 0
# such as switch_debug); the extension controls that movie (play / cue) and
# info_<key> watches it.
SLOTS = {
	'idle': 'null_idle',
	'a': 'null_a',
	'b': 'null_b',
	'tin': 'null_tin',
	'tmid': 'null_tmid',
	'tout': 'null_tout',
}
LOOPING = ('idle',)  # everything else holds its last frame (T_MID is looped by cueing)
BASE_INDEX = {'idle': 0, 'a': 1, 'b': 2}                 # switch_base inputs
TRANS_INDEX = {None: 0, 'tin': 1, 'tmid': 2, 'tout': 3}  # switch_trans inputs (0 = transparent)

BASE_SWITCH = 'switch_base'
TRANS_SWITCH = 'switch_trans'
DEBUG_SWITCH = 'switch_debug'
WS_SERVER = 'webserver_ws'
VARS = 'null_vars'  # variables CHOP: frameDelay = frames the content starts after T_OUT


class MediaServerExt:
	"""State machine: idle loop -> transition -> content -> transition -> idle loop."""

	def __init__(self, ownerComp):
		self.ownerComp = ownerComp

		TDF.createProperty(self, 'State', value=IDLE, dependable=True, readOnly=False)
		TDF.createProperty(self, 'Video', value='', dependable=True, readOnly=False)

		self.direction = None        # TO_ACTIVE | TO_IDLE while transitioning
		self.releasePending = False  # release received, waiting for end of T_MID cycle
		self.returnAt = None         # content index that starts the return (ACTIVE only)
		self.stopPending = False     # stop received during T_OUT, return once ACTIVE
		self.contentToken = 0        # bumped on IDLE, so a delayed content start that's no longer wanted is dropped
		self.Config = {}             # last values received through the config cmd

		# re-init (file edit / reinitextensions) wipes state, so resync the network
		run("args[0]._enterIdle()", self, delayFrames=1)

	# ------------------------------------------------------------------ commands

	def Play(self, video):
		video = str(video).lower()
		if video not in VIDEOS:
			return self._result(False, 'unknown video: {}'.format(video))
		if self.State != IDLE:
			return self._result(False, 'busy')

		self.Video = video
		self.direction = TO_ACTIVE
		self.releasePending = False
		self._cue(video, play=False)  # hold content on frame 0, still hidden
		self._enter(T_IN)
		return self._result(True)

	def Release(self):
		if self.direction is not None and self.State in (T_IN, T_MID):
			self.releasePending = True
			return self._result(True)
		return self._result(False, 'not waiting for release')

	def Stop(self):
		"""Return to IDLE with a transition, without waiting for the content to end."""
		if self.State == ACTIVE:
			self._beginReturn()
			return self._result(True)
		if self.State == T_OUT and self.direction == TO_ACTIVE:
			self.stopPending = True
			return self._result(True)
		return self._result(False, 'not active')

	def Cancel(self):
		self._enterIdle()
		return self._result(True)

	def Reset(self):
		return self.Cancel()

	def Configure(self, values):
		"""Apply startup settings from a client. Each key sets the custom par with the
		same name, capitalized (debug -> Debug). Keys without a par are kept in Config
		and reported once."""
		if not isinstance(values, dict):
			return self._result(False, 'config values must be an object')

		self.Config.update(values)
		missing = []
		for key, value in values.items():
			par = getattr(self.ownerComp.par, str(key).capitalize(), None)
			if par is None:
				missing.append(key)
				continue
			par.val = value

		if 'debug' in values:
			self._setDebug(values['debug'])

		if missing:
			debug('MediaServer: no custom par for config keys', missing)
		return self._result(True)

	# ------------------------------------------------------------------ events

	def OnClipEnd(self, key):
		"""Called by the network when player `key` shows its last frame."""
		s = self.State

		if key == 'tin' and s == T_IN:
			self._enter(T_OUT if self._released() else T_MID)

		elif key == 'tmid' and s == T_MID:
			if self._released():
				self._enter(T_OUT)
			else:
				self._cue('tmid', play=True)  # loop another cycle

		elif key == 'tout' and s == T_OUT:
			if self.direction == TO_ACTIVE:
				self._enter(ACTIVE)
			else:
				self._enterIdle(restart=False)  # idle already running underneath

		elif s == ACTIVE and key == self.Video:
			# backup only: normally the return starts early via OnFrameEnd
			self._beginReturn()

	def OnFrameEnd(self):
		"""Called every frame. Starts the return early, so the content's last
		frame lands under T_IN's last (fully opaque) frame."""
		if self.State != ACTIVE or self.returnAt is None:
			return
		p = self._player(self.Video)
		if p is not None and p.index >= self.returnAt:
			self._beginReturn()

	def _beginReturn(self):
		self.direction = TO_IDLE
		self.returnAt = None
		self._enter(T_IN)  # content keeps playing under the transition

	def _computeReturnAt(self):
		"""Content index at which T_IN must start, or None if unknown.

		T_IN starts the frame after the trigger, so with T_IN length L and
		content length N, triggering at index N-1-L puts both last frames on
		the same displayed frame. Returntrim (frames) shifts it: + = earlier.
		"""
		content, tin = self._player(self.Video), self._player('tin')
		if content is None or tin is None:
			return None
		n, l = content.numImages, tin.numImages
		if n <= 0 or l <= 0:
			return None
		if abs(content.rate - tin.rate) > 0.01:
			debug('MediaServer: video {} is {} fps but T_IN is {} fps - return timing will be off'
				.format(self.Video, content.rate, tin.rate))
		trim = int(self.ownerComp.par.Returntrim.eval())
		return max(0, n - 1 - l - trim)

	def SkipClip(self):
		"""Debug: pretend the clip the current state is waiting on just ended."""
		key = {T_IN: 'tin', T_MID: 'tmid', T_OUT: 'tout', ACTIVE: self.Video}.get(self.State)
		if key:
			self.OnClipEnd(key)
		return self._result(bool(key), None if key else 'nothing to skip in IDLE')

	def onParPulse(self, par):
		if par.name == 'Initpulse':
			self.Reset()

	# ------------------------------------------------------------------ websocket

	def OnClientConnect(self, client):
		self._send(client, self._result(True))  # tell the new client where we are

	def OnClientDisconnect(self, client):
		pass

	def HandleMessage(self, text, client=None):
		try:
			msg = json.loads(text)
			cmd = str(msg.get('cmd', '')).lower()
		except Exception:
			result = self._result(False, 'invalid json')
		else:
			if cmd == 'play':
				result = self.Play(msg.get('video', ''))
			elif cmd == 'stop':
				result = self.Stop()
			elif cmd == 'release':
				result = self.Release()
			elif cmd == 'cancel':
				result = self.Cancel()
			elif cmd == 'state':
				result = self._result(True)
			elif cmd == 'config':
				result = self.Configure(msg.get('values', {}))
			else:
				result = self._result(False, 'unknown cmd: {}'.format(cmd))

		if client is not None:
			self._send(client, result)
		return result

	# ------------------------------------------------------------------ internals

	def _enter(self, state):
		prev = self.State
		self.State = state

		if state == T_IN:
			self._cue('tmid', play=False)
			self._cue('tout', play=False)
			self._cue('tin', play=True)
			self._showTrans('tin')

		elif state == T_MID:
			self._cue('tmid', play=True)
			self._showTrans('tmid')
			self._stop('tin')
			self._swapBase()

		elif state == T_OUT:
			if prev == T_IN:  # T_MID skipped: the swap happens here instead
				self._stop('tin')
				self._swapBase()
			self.releasePending = False
			self._cue('tout', play=True)
			self._showTrans('tout')
			self._stop('tmid')
			if self.direction == TO_ACTIVE:
				self._startContent()  # content starts with the reveal (+ frameDelay)

		elif state == ACTIVE:
			self._showTrans(None)
			self._stop('tout')
			self.returnAt = self._computeReturnAt()

		self._broadcastState()

		if state == ACTIVE and self.stopPending:
			self.stopPending = False
			self._beginReturn()

	def _startContent(self):
		"""Plays the content video, frameDelay frames (null_vars) after T_OUT starts."""
		frames = self._frameDelay()
		if frames <= 0:
			self._play(self.Video)
			return
		run("args[0]._startContentNow(args[1], args[2])",
			self, self.Video, self.contentToken, delayFrames=frames)

	def _startContentNow(self, video, token):
		# Skip if TD went back to IDLE (cancel) or moved on to another video meanwhile.
		if token == self.contentToken and video == self.Video and self.direction == TO_ACTIVE:
			self._play(video)

	def _frameDelay(self):
		chop = self.ownerComp.op(VARS)
		chan = chop['frameDelay'] if chop is not None else None
		return max(0, int(round(chan.eval()))) if chan is not None else 0

	def _swapBase(self):
		"""Screen is fully covered (end of T_IN): swap what's underneath."""
		if self.direction == TO_ACTIVE:
			self._setBase(self.Video)  # paused on frame 0
			self._stop('idle')
		else:
			self._cue('idle', play=True)
			self._setBase('idle')
			self._stop(self.Video)

	def _released(self):
		"""May the transition move on to T_OUT? With no client connected nobody can
		send release, so don't wait for one."""
		return self.releasePending or not self._hasClients()

	def _hasClients(self):
		ws = self.ownerComp.op(WS_SERVER)
		return ws is not None and len(ws.webSocketConnections) > 0

	def _enterIdle(self, restart=True):
		if restart:
			self.ConfigurePlayers()
		self.direction = None
		self.releasePending = False
		self.returnAt = None
		self.stopPending = False
		self.contentToken += 1
		for key in ('a', 'b', 'tin', 'tmid', 'tout'):
			self._stop(key)
		self._showTrans(None)
		if restart:
			self._cue('idle', play=True)
		self._setBase('idle')
		self.Video = ''
		self.State = IDLE
		self._broadcastState()

	# players / switches (safe when ops don't exist yet)

	def _player(self, key):
		"""The moviefileinTOP feeding the slot null, or None. Follows first inputs
		upstream, so a switch in between (e.g. switch_debug before null_idle) is fine."""
		node = self.ownerComp.op(SLOTS.get(key, ''))
		for _ in range(8):
			if node is None or not node.inputs:
				return None
			node = node.inputs[0]
			if node.OPType == 'moviefileinTOP':
				return node
		return None

	def ConfigurePlayers(self):
		"""Put every connected movie into the mode the state machine expects."""
		found = {}
		for key in SLOTS:
			p = self._player(key)
			found[key] = p.path if p else None
			if p is None:
				continue
			p.par.playmode = 'sequential'
			p.par.textendright = 'cycle' if key in LOOPING else 'hold'
		return found

	def _cue(self, key, play):
		p = self._player(key)
		if p is None:
			return
		p.par.cuepoint = 0
		p.par.cuepulse.pulse()
		p.par.play = play

	def _play(self, key):
		p = self._player(key)
		if p is not None:
			p.par.play = True

	def _stop(self, key):
		p = self._player(key)
		if p is not None:
			p.par.play = False

	def _setBase(self, key):
		sw = self.ownerComp.op(BASE_SWITCH)
		if sw is not None:
			sw.par.index = BASE_INDEX[key]

	def _showTrans(self, key):
		sw = self.ownerComp.op(TRANS_SWITCH)
		if sw is not None:
			sw.par.index = TRANS_INDEX[key]

	def _setDebug(self, on):
		"""switch_debug: 0 = normal, 1 = debug."""
		sw = self.ownerComp.op(DEBUG_SWITCH)
		if sw is not None:
			sw.par.index = 1 if on else 0

	# messaging

	def _result(self, ok, error=None):
		r = {'ok': ok, 'state': self.State, 'video': self.Video}
		if error:
			r['error'] = error
		return r

	def _broadcastState(self):
		msg = {'event': 'state', 'state': self.State, 'video': self.Video}
		ws = self.ownerComp.op(WS_SERVER)
		if ws is None:
			return
		for client in ws.webSocketConnections:
			self._send(client, msg)

	def _send(self, client, payload):
		ws = self.ownerComp.op(WS_SERVER)
		if ws is None:
			return
		try:
			ws.webSocketSendText(client, json.dumps(payload))
		except Exception as e:
			debug('MediaServer send failed:', client, e)
