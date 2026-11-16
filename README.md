# samsung-first-look

Projection show for a 3-wall room. TouchDesigner plays the projection and owns the show; Unity renders the scene around the TV and follows TD.

```
projection/   TouchDesigner (player.toe), media in sources/, extension code in code/py/
display/      Unity 2022.3 project (scripts in Assets/_App/Scripts)
```

## Setup

1. Copy the media from `Z:\Projects\85_Samsung 3D TV\06_Motion\projection\sources` to `./projection/sources` (not in git). Every TD machine needs its own copy.
2. Set each machine's config file (see below).

| Machine | Runs | Config |
|---|---|---|
| TD master | `projection/player.toe`, hosts the WebSocket (port 9980) | `projection/Assets/Config.json` (`isMaster: true`) |
| TD (additional renderer, optional) | same `player.toe`, follows the master over Touch In/Out | same file, `isMaster: false` |
| Unity | the display build, connects to the TD master | `display/Assets/StreamingAssets/Config.json` (`serverUrl`), `StagingConfig.json` (camera) |

Unity sends its effects layer to TD over NDI (`UnityFX`).

## Logic

TD (`/project1/media_server`, `MediaServerExt.py`) runs one state machine:

```
IDLE -> T_IN -> T_MID (loops until Unity is ready) -> T_OUT -> ACTIVE -> back to IDLE
```

- Timer CHOPs drive the frame indices of the movies; Python only decides what comes next and talks to Unity.
- The master sends the timer and state channels to the additional renderer over Touch Out, so both play the same frame.
- Unity loads the scene TD names, replies `ready` while TD is in T_IN / T_MID, and plays it once TD reveals it (T_OUT).

WebSocket messages (JSON):

```
TD -> Unity   {"event": "state", "state": "T_IN", "scene": "a"}   on every change
Unity -> TD   {"cmd": "request", "scene": "a"}                   ask for a scene
              {"cmd": "ready",   "scene": "a"}                   scene loaded
              {"cmd": "config",  "values": {"debug": true}}      on connect
```

## Keys (Unity)

| Key | Action |
|---|---|
| 1 / 2 / 3 | request A / B / Idle |
| Ctrl+Shift+R | restart |
| Ctrl+Shift+Q | quit |

Without a TD connection the keys load scenes directly.
