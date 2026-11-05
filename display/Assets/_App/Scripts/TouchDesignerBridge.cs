using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klak.Ndi;
using UnityEngine;

#region MESSAGE TYPES

// Matches both TD replies and broadcasts. "event" is a C# keyword, hence the @.
[Serializable]
public class TdMessage
{
    public string @event;
    public bool ok;
    public string state;
    public string video;
    public string error;
}

/// <summary>
/// Startup settings sent with the config cmd. Add fields here (e.g. public float offsetx;)
/// and fill them in BuildConfig(); field names are the keys TD receives.
/// </summary>
/// 
[Serializable]
public class TdConfig
{
    public bool debug;
}

#endregion

/// <summary>
/// Single point of contact between Unity and the TouchDesigner media server
/// (/project1/media_server, see MediaServerExt in TD).
///
/// Video: Unity -> TD over NDI through a KlakNDI NdiSender (TD's ndiin2 listens for "UnityFX").
/// Control: a WebSocket client connected to TD's webserver_ws DAT, speaking MediaServerExt's protocol:
///   -> {"cmd": "play", "video": "a"}   only accepted in IDLE
///   -> {"cmd": "stop"}                 return to IDLE with a transition (ACTIVE or T_OUT going in)
///   -> {"cmd": "release"}              ends T_MID (during T_IN or T_MID, both ways)
///   -> {"cmd": "cancel"}               hard cut to IDLE
///   -> {"cmd": "state"}                ask for the current state
///   -> {"cmd": "config", "values": {"debug": true}}   startup settings, sent on every connect
///
///   &lt;- {"ok": true|false, "state": ..., "video": ..., "error"?: ...}   reply to a command (and on connect)
///   &lt;- {"event": "state", "state": ..., "video": ...}                broadcast on every change
///
/// TD states: IDLE, T_IN, T_MID, T_OUT, ACTIVE.
/// </summary>
public class TouchDesignerBridge : MonoBehaviour
{
    #region VARIABLES

    public static TouchDesignerBridge Instance { get; private set; }

    public const string Idle = "IDLE", TIn = "T_IN", TMid = "T_MID", TOut = "T_OUT", Active = "ACTIVE";

    [Header("WebSocket")]
    [SerializeField] private float reconnectDelay = 2f;

    [Header("NDI")]
    [SerializeField] private NdiSender ndiSender;
    [SerializeField] private string ndiName = "UnityFX";

    // TD's address, from Config.json (serverUrl).
    public string ServerUrl => AppManager.Instance != null ? AppManager.Instance.config.serverUrl : new RendererConfig().serverUrl;

    public bool IsConnected => socket != null && socket.State == WebSocketState.Open;
    public bool CanPlay => !IsConnected || State == Idle;  // TD accepts play only in IDLE
    public string State { get; private set; } // Last known TD state (IDLE, T_IN, T_MID, T_OUT, ACTIVE), or null before the first message.
    public string Video { get; private set; } // Video TD is playing or transitioning to ("a", "b"), or empty.

    private ClientWebSocket socket;
    private CancellationTokenSource cts;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private bool hasSynced;  // first TD state since this app (re)started has been handled

    #endregion

    #region EVENTS

    public event Action Connected;
    public event Action Disconnected;
    public event Action<string, string> StateChanged; // (state, video). Fired whenever TD reports a different state or video.
    public event Action<string> CommandFailed; // Fired with TD's error text when it rejects a command.

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[TouchDesignerBridge] Duplicate instance found, destroying this one.", this);
            enabled = false;  // keeps OnEnable from opening a second connection before the destroy lands
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnEnable()
    {
        cts = new CancellationTokenSource();
        _ = ConnectionLoop(cts.Token);
    }

    void Start()
    {
        if (ndiSender != null && !string.IsNullOrEmpty(ndiName))
            ndiSender.ndiName = ndiName;

        if (SceneLoader.Instance != null)
        {
            SceneLoader.Instance.TransitionStarted += OnTransitionStarted;
            SceneLoader.Instance.SceneLoaded += OnSceneLoaded;
        }
    }

    void OnDisable()
    {
        // Not disposed on purpose: the connection loop may still be reading the token while it unwinds.
        cts?.Cancel();
        cts = null;
    }

    void OnDestroy()
    {
        if (SceneLoader.Instance != null)
        {
            SceneLoader.Instance.TransitionStarted -= OnTransitionStarted;
            SceneLoader.Instance.SceneLoaded -= OnSceneLoaded;
        }

        if (Instance == this) Instance = null;
    }

    #endregion

    #region COMMANDS

    // Starts TD's transition to video "a" or "b". Only accepted while TD is IDLE.
    public void SendPlay(string _video)
    {
        SendCommand($"{{\"cmd\":\"play\",\"video\":{Quote(_video)}}}");
    }

    // Starts TD's return to IDLE with a transition (ACTIVE, or T_OUT on the way in).
    public void SendStop()
    {
        SendCommand("{\"cmd\":\"stop\"}");
    }

    // Lets TD leave its T_IN / T_MID loop and reveal the content.
    public void SendRelease()
    {
        SendCommand("{\"cmd\":\"release\"}");
    }

    // Hard cut to IDLE, no transition.
    public void SendCancel()
    {
        SendCommand("{\"cmd\":\"cancel\"}");
    }

    // Asks TD for its current state; the answer arrives through StateChanged.
    public void RequestState()
    {
        SendCommand("{\"cmd\":\"state\"}");
    }

    #endregion

    #region CONFIG

    /// <summary>
    /// Sends the startup settings to TD. Runs on every (re)connect; call it again after
    /// changing a value at runtime. TD maps each field onto media_server's custom parameter
    /// with the same name, capitalized (debug -> Debug).
    /// </summary>
    public void SendConfig()
    {
        SendCommand($"{{\"cmd\":\"config\",\"values\":{JsonUtility.ToJson(BuildConfig())}}}");
    }

    private TdConfig BuildConfig()
    {
        AppManager app = AppManager.Instance;
        return new TdConfig
        {
            debug = app != null && app.config.useDebug,
        };
    }

    #endregion

    #region OUTGOING MESSAGES

    private async void SendCommand(string _json)
    {
        ClientWebSocket ws = socket;
        if (ws == null || ws.State != WebSocketState.Open)
        {
            Debug.Log($"[TouchDesignerBridge] Not connected, dropping {_json}");
            return;
        }

        Debug.Log($"[TouchDesignerBridge] -> {_json}");

        byte[] bytes = Encoding.UTF8.GetBytes(_json);
        CancellationToken token = cts?.Token ?? CancellationToken.None;

        // ClientWebSocket doesn't allow overlapping sends.
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning($"[TouchDesignerBridge] Send failed: {e.Message}");
        }
        finally
        {
            sendLock.Release();
        }
    }

    #endregion

    #region INCOMING MESSAGES

    private void HandleMessage(string _json)
    {
        Debug.Log($"[TouchDesignerBridge] <- {_json}");

        TdMessage msg;
        try
        {
            msg = JsonUtility.FromJson<TdMessage>(_json);
        }
        catch (Exception)
        {
            Debug.LogWarning($"[TouchDesignerBridge] Ignoring message that isn't valid JSON: {_json}");
            return;
        }

        if (msg == null || string.IsNullOrEmpty(msg.state))
            return;

        // Replies to commands carry "ok"; broadcasts carry "event" instead.
        bool isReply = string.IsNullOrEmpty(msg.@event);
        if (isReply && !msg.ok)
        {
            Debug.LogWarning($"[TouchDesignerBridge] TD rejected command: {msg.error} (state {msg.state})");
            CommandFailed?.Invoke(msg.error);
        }

        // The app (re)starts in Idle, so a TD left mid-show by an earlier run is cut back to IDLE.
        // Only on the first state of this run; later reconnects (network drops) keep TD's state.
        if (!hasSynced)
        {
            hasSynced = true;
            if (msg.state != Idle)
                SendCancel();
        }

        string previous = State;
        string video = msg.video ?? "";
        if (msg.state == State && video == Video)
            return;

        State = msg.state;
        Video = video;
        StateChanged?.Invoke(State, Video);

        FollowTdState(previous, State);
    }

    #endregion

    #region CONNECTION

    // Runs on the main thread: Unity's SynchronizationContext resumes awaits there.
    private async Task ConnectionLoop(CancellationToken _token)
    {
        bool loggedFailure = false;

        // OnEnable can run before AppManager's Awake has read Config.json; wait a frame for serverUrl.
        await Task.Yield();

        while (!_token.IsCancellationRequested)
        {
            using (var ws = new ClientWebSocket())
            {
                bool wasConnected = false;
                string url = ServerUrl;
                try
                {
                    await ws.ConnectAsync(new Uri(url), _token);
                    socket = ws;
                    wasConnected = true;
                    loggedFailure = false;

                    // TD replies with its current state on connect, no need to ask.
                    Debug.Log($"[TouchDesignerBridge] Connected to {url}");
                    SendConfig();
                    Connected?.Invoke();

                    await ReceiveLoop(ws, _token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // Log once per outage instead of on every retry.
                    if (!loggedFailure)
                    {
                        Debug.LogWarning($"[TouchDesignerBridge] Can't reach {url} ({e.Message}). Retrying every {reconnectDelay}s.");
                        loggedFailure = true;
                    }
                }
                finally
                {
                    socket = null;
                    if (wasConnected)
                    {
                        Debug.Log("[TouchDesignerBridge] Disconnected");
                        if (!_token.IsCancellationRequested)
                        {
                            // No T_OUT will arrive now; don't leave a loaded scene waiting.
                            if (SceneLoader.Instance != null)
                                SceneLoader.Instance.PlayScene();
                            Disconnected?.Invoke();
                        }
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), _token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket _ws, CancellationToken _token)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);

        using (var message = new MemoryStream())
        {
            while (_ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(buffer, _token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }

                message.Write(buffer.Array, 0, result.Count);

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));

                message.SetLength(0);
            }
        }
    }

    #endregion

    #region SCENE SYNC

    private void OnTransitionStarted(string _sceneName)
    {
        string video = VideoForScene(_sceneName);
        if (video != null)
            SendPlay(video);
        else if (_sceneName == SceneLoader.Instance.IdleScene && (State == Active || State == TOut))
            SendStop();  // Unity heads back to Idle first (key 3): take TD back with it
    }

    /// <summary>
    /// TD holds its transition (T_IN / T_MID) until Unity releases it, in both directions,
    /// and starts its video when T_OUT begins; the scene plays then (see FollowTdState).
    /// If TD isn't waiting for a release there's nothing to sync to, so the scene plays now.
    /// </summary>
    private void OnSceneLoaded(string _sceneName)
    {
        if (IsConnected && (State == TIn || State == TMid)) SendRelease();
        else SceneLoader.Instance.PlayScene();
    }

    private void FollowTdState(string _previous, string _current)
    {
        SceneLoader loader = SceneLoader.Instance;
        if (loader == null)
            return;

        if (_current == TOut)
            loader.PlayScene();   // TD starts its video now: play Unity's scene in the same frame

        if (_previous == null)
            return;

        bool returning = _previous == Active && _current == TIn;  // content ended (or stop): TD heads back to idle
        bool hardCut = _current == Idle && _previous != TOut;     // cancel from TD (T_OUT -> IDLE is the normal return)
        bool tdWaiting = _current == TIn || _current == TMid;     // TD waits for a release
        bool unityInIdle = loader.CurrentScene == loader.IdleScene;

        // Follow TD back to Idle. Already on the way (key 3) = IsTransitioning, nothing to do.
        if ((returning || hardCut) && !loader.IsTransitioning && !unityInIdle)
            loader.LoadIdle();
        // Unity's Idle is already loaded, so OnSceneLoaded won't come to release TD: do it now.
        else if (tdWaiting && unityInIdle && !loader.IsLoading)
            SendRelease();
    }

    private static string VideoForScene(string _sceneName)
    {
        SceneLoader loader = SceneLoader.Instance;
        if (loader == null)
            return null;
        if (_sceneName == loader.AScene) return "a";
        if (_sceneName == loader.BScene) return "b";
        return null;
    }

    #endregion

    #region HELPERS

    private static string Quote(string _value)
    {
        if (_value == null)
            return "null";

        var sb = new StringBuilder(_value.Length + 2);
        sb.Append('"');
        foreach (char c in _value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    #endregion
}
