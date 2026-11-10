using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.IO;
using System;

[Serializable]
public class RendererConfig 
{
    public int targetFrameRate = 60;
    public bool useVSync = false;
    public bool useDebug = false;
    public string serverUrl = "ws://127.0.0.1:9980";
    public int ndiLatencyFrames = 0;
}

[Serializable]
public class StagingConfig
{
    public float cropFactor = 10f;
    public float shiftX = 0f;
    public float shiftY = -0.06f;
}

public class AppManager : MonoBehaviour
{
    #region VARIABLES

    public static AppManager Instance { get; private set; }

    public RendererConfig config = new RendererConfig();
    public bool SettingsLoaded { get; private set; }  // Config.json and StagingConfig.json have been read
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
            if (Input.GetKeyDown(KeyCode.R)) ReloadScene();
            else if (Input.GetKeyDown(KeyCode.Q)) QuitApp();
            return;
        }
    }

    #endregion

    private IEnumerator LoadSettingsAsync()
    {
        string basePath = Application.streamingAssetsPath;

        // config
        yield return LoadJson(
            Path.Combine(basePath, "Config.json"),
            json =>
            {
                config = JsonUtility.FromJson<RendererConfig>(json);
            }
        );

        // staging
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
        SettingsLoaded = true;
        if (Application.isPlaying) ApplyFrameRate();

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
