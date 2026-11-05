using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.IO;
using System;

/// <summary>
/// AppManager and its children survive scene loads; everything else is replaced.
/// A scene that contains another AppManager (e.g. reloading the first scene) gets its copy
/// removed, children included.
/// </summary>

[Serializable]
public class RendererConfig 
{
    public int targetFrameRate = 60;
    public bool useVSync = false;
    public bool useDebug = false;
    public string serverUrl = "ws://127.0.0.1:9980";  // TD machine's webserver_ws (MediaServer's Port parameter)
    public int ndiLatencyFrames = 0;                  // scenes start this many frames ahead to make up for the NDI delay to TD
}

// Per-venue camera alignment (StagingConfig.json), applied by StagingCamera.
[Serializable]
public class StagingConfig
{
    public float sensorSizeX = 10f;  // physical camera sensor width (mm); the height stays as set on the camera
    public float shiftX = 0f;        // lens shift, in fractions of the sensor size
    public float shiftY = -0.06f;
}

public class AppManager : MonoBehaviour
{
    #region VARIABLES

    public static AppManager Instance { get; private set; }

    public RendererConfig config = new RendererConfig();
    public StagingConfig staging = new StagingConfig();

    public GameObject debugGrid;

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        // hide cursor
        #if !UNITY_EDITOR
            Cursor.visible = false;
        #endif

        if (Instance != null && Instance != this)
        {
            // Deactivate first so children that haven't woken up yet never run Awake/Start.
            gameObject.SetActive(false);
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);  // only works on root objects, so keep AppManager at the root

        StartCoroutine(LoadSettingsAsync());
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (ctrl && shift)
        {
            if (Input.GetKeyDown(KeyCode.R))
                ReloadScene();
            else if (Input.GetKeyDown(KeyCode.Q))
                QuitApp();
            return;
        }

        // Scene keys: 1 = A, 2 = B, 3 = back to Idle (top row or numpad)
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            LoadA();
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            LoadB();
        else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            LoadIdle();
    }

    #endregion

    private IEnumerator LoadSettingsAsync()
    {
        string basePath = Application.streamingAssetsPath;

        yield return LoadJson(
            Path.Combine(basePath, "Config.json"),
            json =>
            {
                config = JsonUtility.FromJson<RendererConfig>(json);
            }
        );

        yield return LoadJson(
            Path.Combine(basePath, "StagingConfig.json"),
            json =>
            {
                staging = JsonUtility.FromJson<StagingConfig>(json);
            }
        );

        Initialize();
    }


    #region SETUP

    void Initialize()
    {
        if (Application.isPlaying) ApplyFrameRate();

        // useDebug reaches TouchDesigner through TouchDesignerBridge's config message,
        // which is sent every time the bridge connects.
        if (config.useDebug)
        {
            debugGrid.SetActive(true);
        }
        else
        {
            debugGrid.SetActive(false);
        }
    }

    private void ApplyFrameRate()
    {
        QualitySettings.vSyncCount = config.useVSync ? 1 : 0;
        Application.targetFrameRate = config.targetFrameRate;
    }

    #endregion

    #region SCENES

    public void LoadA()
    {
        if (SceneLoader.Instance != null && TdCanPlay())
            SceneLoader.Instance.LoadA();
    }

    public void LoadB()
    {
        if (SceneLoader.Instance != null && TdCanPlay())
            SceneLoader.Instance.LoadB();
    }

    // TD only accepts play in IDLE; starting A/B otherwise would leave TD behind.
    private bool TdCanPlay()
    {
        TouchDesignerBridge bridge = TouchDesignerBridge.Instance;
        if (bridge == null || bridge.CanPlay)
            return true;

        Debug.Log($"[AppManager] TD is {bridge.State}, not IDLE: ignoring scene request.");
        return false;
    }

    // Transitions back to Idle (TouchDesignerBridge tells TD to return too).
    public void LoadIdle()
    {
        if (SceneLoader.Instance != null)
            SceneLoader.Instance.LoadIdle();
    }

    /// <summary>
    /// Full restart: reloads the first scene and destroys this AppManager, so the scene's own
    /// AppManager starts fresh. The new TouchDesignerBridge brings TD back to IDLE when it connects.
    /// </summary>
    public void ReloadScene()
    {
        // A sync load can't run while SceneLoader's background load is waiting for activation.
        if (SceneLoader.Instance != null && SceneLoader.Instance.IsTransitioning)
            return;

        SceneManager.LoadScene(0);  // loads next frame, after this AppManager is gone
        Destroy(gameObject);
    }

    
    public void QuitApp()
    {
        #if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
        #else
                Application.Quit();
        #endif
    }

    #endregion

    #region HELPERS

    private IEnumerator LoadJson(string path, Action<string> onLoaded)
    {
        if (!File.Exists(path))
        {
            Debug.LogError($"[AppManager] Config file not found: {path}");
            yield break;
        }

        Debug.Log($"[AppManager] Reading {Path.GetFileName(path)}");

        string json = string.Empty;

        try
        {
            // Disk I/O (still sync, but isolated)
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AppManager] Failed to read file: {e}");
            yield break;
        }

        if (string.IsNullOrEmpty(json))
        {
            Debug.LogError($"[AppManager] Empty config file: {path}");
            yield break;
        }

        onLoaded?.Invoke(json);

        yield return null;
    }

    #endregion
}
