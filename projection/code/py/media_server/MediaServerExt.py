"""
MediaServerExt - owns the show. Unity follows what TD says.

The nodes do the timing, this extension only decides what comes next and talks to Unity:
  timer_trans    T_IN -> T_MID (loops) -> T_OUT, one segment per movie (table_trans).
                 Its timer_frames is the index of t_in / t_mid / t_out, its segment picks switch_trans.
  timer_content  plays active_a / active_b (index = timer_frames). Starts frameDelay frames
                 (null_vars) after T_OUT and alerts T_IN's length before its end, so the return
                 starts with the content's last frame under T_IN's last (fully opaque) frame.
  constant_state base (switch_base), content (which of active_a / active_b plays) and length
                 (timer_content), written here. Nodes read state from this CHOP, not from
                 extension properties, so they always recook.
  Movie lengths come from info CHOPs, read once (table_trans on init, length per run): a
  live link would make the timers depend on the movies they drive (a cook loop).

States
  IDLE -> T_IN -> T_MID (loops until Unity is ready) -> T_OUT -> ACTIVE
  ACTIVE -> T_IN -> T_MID (loops until Unity is ready) -> T_OUT -> IDLE
  Ready during T_IN skips T_MID. With no client connected T_MID plays once.

Protocol (JSON over WebSocket, TD is the host)
  <- {"event": "state", "state": "T_IN", "scene": "a"}
       on every change and on connect. scene is what Unity should show: load it when it
       changes, play it once the state is T_OUT, ACTIVE or IDLE.
  -> {"cmd": "ready", "scene": "a"}      Unity has loaded and reset the scene (ends T_MID)
  -> {"cmd": "request", "scene": "a"}    ask for a scene: a|b from IDLE, idle from ACTIVE
  -> {"cmd": "cancel"}                   hard cut to IDLE
  -> {"cmd": "state"}                    ask for the current state
  -> {"cmd": "config", "values": {"debug": true}}
                                         startup settings. Each key sets the media_server
                                         custom par of the same name, capitalized (debug -> Debug)
  <- {"ok": true|false, "state": ..., "scene": ..., "error"?: ...}   reply to every command
"""

import json

IDLE, T_IN, T_MID, T_OUT, ACTIVE = 'IDLE', 'T_IN', 'T_MID', 'T_OUT', 'ACTIVE'
CONTENT = ('a', 'b')
BASES = ('idle', 'a', 'b')  # switch_base inputs
SEG_TMID, SEG_TOUT = 1, 2  # timer_trans segments (rows of table_trans)


class MediaServerExt:

	def __init__(self, ownerComp):
		self.ownerComp = ownerComp

		self.State = IDLE
		self.Scene = 'idle'       # what Unity should show
		self.Base = 'idle'        # what's under the transition (constant_state base -> switch_base)
		self.ready = False        # Unity has loaded Scene
		self.stopPending = False  # idle requested during T_OUT on the way in

		# re-init (file edit / reinitextensions) wipes state, so resync the network
		run("args[0].Cancel()", self, delayFrames=1)

	#region COMMANDS

	def Request(self, scene):
		scene = str(scene).lower()
		if scene in CONTENT:
			if self.State != IDLE:
				return self._result(False, 'busy')
			self._setContent(scene)
			self._transitionTo(scene)
		elif scene == 'idle':
			if self.State == ACTIVE:
				self._transitionTo('idle')
			elif self.State == T_OUT and self.Scene != 'idle':
				self.stopPending = True
			else:
				return self._result(False, 'not playing content')
		else:
			return self._result(False, 'unknown scene: {}'.format(scene))
		return self._result(True)

	def Ready(self, scene):
		if self.State not in (T_IN, T_MID) or str(scene).lower() != self.Scene:
			return self._result(False, 'not waiting for {}'.format(scene))
		self.ready = True
		timer = self.ownerComp.op('timer_trans')
		if self.State == T_IN:
			timer.goTo(segment=SEG_TOUT, endOfCycle=True)  # skip T_MID
		else:
			timer.lastCycle()  # finish this T_MID loop, then T_OUT
		return self._result(True)

	def Cancel(self):
		self._updateLengths()
		self.ownerComp.op('timer_trans').par.initialize.pulse()
		self.ownerComp.op('timer_content').par.initialize.pulse()
		self.ready = False
		self.stopPending = False
		self.Scene = 'idle'
		self._setBase('idle', force=True)
		self._enter(IDLE)
		return self._result(True)

	def Configure(self, values):
		if not isinstance(values, dict):
			return self._result(False, 'config values must be an object')
		for key, value in values.items():
			par = getattr(self.ownerComp.par, str(key).capitalize(), None)
			if par is None:
				debug('MediaServer: no custom par for config key', key)
				continue
			par.val = value
		return self._result(True)

	def onParPulse(self, par):
		if par.name == 'Initpulse':
			self.Cancel()

	#endregion

	#region TIMER EVENTS

	# timer_trans entered a segment (timer_trans_callbacks)
	def OnTransSegment(self, segment):
		if self.State not in (T_IN, T_MID):
			return
		if segment == SEG_TMID:
			self._setBase(self.Scene)  # screen fully covered: swap what's underneath
			self._enter(T_MID)
			if self.ready or not self._hasClients():
				self.ownerComp.op('timer_trans').lastCycle()
		elif segment == SEG_TOUT:
			self._setBase(self.Scene)  # only does something when T_MID was skipped
			self._enter(T_OUT)
			if self.Scene in CONTENT:
				self.ownerComp.op('timer_content').par.start.pulse()

	# timer_trans finished T_OUT
	def OnTransDone(self):
		if self.State != T_OUT:
			return
		if self.Scene == 'idle':
			self._enter(IDLE)
			return
		self._enter(ACTIVE)
		if self.stopPending:
			self.stopPending = False
			self._transitionTo('idle')

	# timer_content is T_IN's length from its end
	def OnContentEnding(self):
		if self.State == ACTIVE:
			self._transitionTo('idle')

	#endregion

	#region WEBSOCKET

	def OnClientConnect(self, client):
		self._send(client, self._stateMessage())

	def OnClientDisconnect(self, client):
		pass

	def HandleMessage(self, text, client=None):
		try:
			msg = json.loads(text)
			cmd = str(msg.get('cmd', '')).lower()
		except Exception:
			result = self._result(False, 'invalid json')
		else:
			if cmd == 'request':
				result = self.Request(msg.get('scene', ''))
			elif cmd == 'ready':
				result = self.Ready(msg.get('scene', ''))
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

	#endregion

	#region INTERNALS

	def _transitionTo(self, scene):
		self.Scene = scene
		self.ready = False
		self._enter(T_IN)
		self.ownerComp.op('timer_trans').par.start.pulse()

	def _enter(self, state):
		self.State = state
		self.ownerComp.par.State = state
		self.ownerComp.par.Scene = self.Scene
		for client in self.ownerComp.op('webserver_ws').webSocketConnections:
			self._send(client, self._stateMessage())

	def _setBase(self, scene, force=False):
		if scene == self.Base and not force:
			return
		if scene == 'idle':
			self.ownerComp.op('idle_loop').par.cuepulse.pulse()  # idle restarts from its first frame
		else:
			self.ownerComp.op('timer_content').par.initialize.pulse()  # content waits on frame 0
		self.Base = scene
		self.ownerComp.op('constant_state').par.value0 = BASES.index(scene)  # switch_base

	# Copies the transition movie lengths into table_trans (timer_trans segments). Plain numbers,
	# so the running timer doesn't make the info CHOPs decode every movie each frame.
	def _updateLengths(self):
		table = self.ownerComp.op('table_trans')
		for row, info in (('tin', 'info_tin'), ('tmid', 'info_tmid'), ('tout', 'info_tout')):
			table[row, 'length'] = int(self.ownerComp.op(info)['true_length'])

	# Picks the content video of this run and copies its length for timer_content.
	def _setContent(self, scene):
		info = self.ownerComp.op('info_content')
		info.par.op = 'active_' + scene
		state = self.ownerComp.op('constant_state').par
		state.value1 = CONTENT.index(scene)
		state.value2 = int(info['true_length'])

	def _hasClients(self):
		return len(self.ownerComp.op('webserver_ws').webSocketConnections) > 0

	def _result(self, ok, error=None):
		r = {'ok': ok, 'state': self.State, 'scene': self.Scene}
		if error:
			r['error'] = error
		return r

	def _stateMessage(self):
		return {'event': 'state', 'state': self.State, 'scene': self.Scene}

	def _send(self, client, payload):
		try:
			self.ownerComp.op('webserver_ws').webSocketSendText(client, json.dumps(payload))
		except Exception as e:
			debug('MediaServer send failed:', client, e)

	#endregion
