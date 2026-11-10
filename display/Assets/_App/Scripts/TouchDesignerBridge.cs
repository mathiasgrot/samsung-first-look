using System;
using System.Collections;
using UnityEngine;

#region MESSAGE TYPES

// Matches both TD replies and broadcasts. "event" is a C# keyword, hence the @.
[Serializable]
public class TdMessage
{
    public string @event;
    public bool ok;
    public string state;
    public string scene;
    public string error;
}

// Commands with a scene: request and ready.
[Serializable]
public class TdCommand
{
    public string cmd;
    public string scene;
}

// Startup settings sent with the config cmd. Field names are the keys TD receives.
[Serializable]
public class TdConfig
{
    public bool debug;
}

#endregion

/// <summary>
/// Unity's side of the TouchDesigner media server protocol (/project1/media_server, see
/// MediaServerExt in TD). TD owns the show, Unity follows. The connection itself is WebSocketClient.
///
///   &lt;- {"event": "state", "state": "T_IN", "scene": "a"}   on every change and on connect
///   -> {"cmd": "ready", "scene": "a"}      scene loaded and reset (TD ends T_MID)
///   -> {"cmd": "request", "scene": "a"}    keys: ask TD for a scene, TD decides
///   -> {"cmd": "config", "values": {"debug": true}}   sent on every connect
///   &lt;- {"ok": true|false, "state": ..., "scene": ..., "error"?: ...}   reply to every command
///
/// Following TD: load TD's scene, say ready once it's loaded while TD waits (T_IN / T_MID),
/// play it once TD reveals it (T_OUT / ACTIVE / IDLE). Without TD, keys load scenes directly.
/// </summary>


public class TouchDesignerBridge : MonoBehaviour
{
    #region VARIABLES

    public static TouchDesignerBridge Instance { get; private set; }

    public const string Idle = "IDLE", TIn = "T_IN", TMid = "T_MID", TOut = "T_OUT", Active = "ACTIVE";

    [SerializeField] private WebSocketClient client;

    public string ServerUrl => AppManager.Instance != null ? AppManager.Instance.config.serverUrl : new RendererConfig().serverUrl;

    public bool IsConnected => client.IsConnected;
    public string State { get; private set; } // last TD state (IDLE, T_IN, T_MID, T_OUT, ACTIVE), null before the first message
    public string Scene { get; private set; } // scene TD wants shown ("idle", "a", "b")

    private bool TdWaiting => State == TIn || State == TMid;  // TD holds its transition until we're ready
    private bool readySent;  // ready already sent for the transition TD is holding

    #endregion

    #region EVENTS

    public event Action<string, string> StateChanged; // (state, scene), fired whenever TD reports a different one
    public event Action<string> CommandFailed; // TD's error text when it rejects a command

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[TouchDesignerBridge] Duplicate instance found, destroying this one.", this);
            enabled = false;  // keeps Start from opening a second connection before the destroy lands
            Destroy(gameObject);
            return;
        }
        Instance = this;

        client.Connected += OnConnected;
        client.Disconnected += OnDisconnected;
        client.MessageReceived += HandleMessage;
    }

    IEnumerator Start()
    {
        SceneLoader.Instance.SceneLoaded += OnSceneLoaded;

        // serverUrl and useDebug come from Config.json
        yield return new WaitUntil(() => AppManager.Instance.SettingsLoaded);
        client.Open(ServerUrl);
    }

    void OnDestroy()
    {
        client.Connected -= OnConnected;
        client.Disconnected -= OnDisconnected;
        client.MessageReceived -= HandleMessage;

        if (SceneLoader.Instance != null)
            SceneLoader.Instance.SceneLoaded -= OnSceneLoaded;

        if (Instance == this) Instance = null;
    }

    #endregion

    #region COMMANDS

    // Asks TD for a scene (keys). TD accepts A/B from IDLE and Idle while content plays.
    public void RequestScene(string _sceneName)
    {
        if (IsConnected)
            Send(JsonUtility.ToJson(new TdCommand { cmd = "request", scene = KeyFor(_sceneName) }));
        else
            SceneLoader.Instance.LoadScene(_sceneName);
    }

    // Tells TD the scene is loaded and reset, so it can leave T_MID.
    private void SendReady(string _key)
    {
        Send(JsonUtility.ToJson(new TdCommand { cmd = "ready", scene = _key }));
    }

    // Sends the startup settings. TD sets the media_server custom par with the same name, capitalized (debug -> Debug).
    public void SendConfig()
    {
        TdConfig config = new TdConfig { debug = AppManager.Instance != null && AppManager.Instance.config.useDebug };
        Send($"{{\"cmd\":\"config\",\"values\":{JsonUtility.ToJson(config)}}}");
    }

    private void Send(string _json)
    {
        Debug.Log($"[TouchDesignerBridge] -> {_json}");
        client.Send(_json);
    }

    #endregion

    #region CONNECTION EVENTS

    private void OnConnected()
    {
        State = Scene = null;  // TD sends its state on connect, and the first one is always followed
        readySent = false;
        SendConfig();
    }

    private void OnDisconnected()
    {
        SceneLoader.Instance.PlayScene();  // TD can't reveal the scene now, don't leave it waiting
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

        // Replies to commands carry "ok", broadcasts carry "event" instead.
        if (string.IsNullOrEmpty(msg.@event) && !msg.ok)
        {
            Debug.LogWarning($"[TouchDesignerBridge] TD rejected command: {msg.error} (state {msg.state})");
            CommandFailed?.Invoke(msg.error);
        }

        if (msg.state == State && msg.scene == Scene)
            return;

        State = msg.state;
        Scene = msg.scene;
        StateChanged?.Invoke(State, Scene);
        FollowTd();
    }

    #endregion

    #region FOLLOW TD

    // Brings Unity to what TD says. Runs on every TD state change and whenever a scene finishes loading.
    private void FollowTd()
    {
        SceneLoader loader = SceneLoader.Instance;
        string scene = SceneFor(Scene);
        if (scene == null)
            return;

        if (!TdWaiting)
            readySent = false;

        if (loader.TargetScene != scene)
        {
            // TD changed its mind mid-transition (cancel): finish the old scene, the new one loads after it.
            if (loader.IsTransitioning)
                loader.PlayScene();
            loader.LoadScene(scene);
            return;
        }

        bool loaded = loader.CurrentScene == scene && !loader.IsLoading;
        if (!loaded)
            return;

        if (!TdWaiting)
        {
            loader.PlayScene();
        }
        else if (!readySent)
        {
            readySent = true;
            SendReady(Scene);
        }
    }

    private void OnSceneLoaded(string _sceneName)
    {
        if (IsConnected) FollowTd();
        else SceneLoader.Instance.PlayScene();  // nobody to sync with
    }

    #endregion

    #region HELPERS

    // TD's scene keys <-> Unity scene names.
    private static string SceneFor(string _key)
    {
        SceneLoader loader = SceneLoader.Instance;
        switch (_key)
        {
            case "idle": return loader.IdleScene;
            case "a": return loader.AScene;
            case "b": return loader.BScene;
            default: return null;
        }
    }

    private static string KeyFor(string _sceneName)
    {
        SceneLoader loader = SceneLoader.Instance;
        if (_sceneName == loader.AScene) return "a";
        if (_sceneName == loader.BScene) return "b";
        return "idle";
    }

    #endregion
}
