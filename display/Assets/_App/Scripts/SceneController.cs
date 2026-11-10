using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

/// <summary>
/// One per scene: owns what the scene plays
///
/// SceneLoader calls ResetScene() as soon as the scene is loaded, waits until IsReady,
/// then calls Play() when it's told to (TD entering T_OUT, through TouchDesignerBridge).
/// </summary>

public class SceneController : MonoBehaviour
{
    #region VARIABLES

    private static readonly List<SceneController> controllers = new List<SceneController>();

    [SerializeField] private VideoPlayer video;
    [SerializeField] private Camera displayCamera;  // aligned to StagingConfig.json on load

    // Reset, prepared and on the start frame, so Play() starts straight away.
    public bool IsReady { get; private set; }
    private bool cameraSettingApplied;

    private int StartFrame => AppManager.Instance != null ? AppManager.Instance.config.ndiLatencyFrames : 0;

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        video.playOnAwake = false;
    }

    void OnEnable() => controllers.Add(this);
    void OnDisable() => controllers.Remove(this);

    #endregion

    #region SCENE

    // Cameras aligned, video back to the start frame, paused, and getting ready to play.
    public void ResetScene()
    {
        ApplyCameraSettings();

        IsReady = false;
        video.Stop();
        video.prepareCompleted -= OnPrepared;
        video.prepareCompleted += OnPrepared;
        video.Prepare();
    }

    public void Play()
    {
        video.Play();
    }

    // The controller in a scene, or null if the scene has none.
    public static SceneController InScene(Scene _scene)
    {
        SceneController found = null;
        foreach (SceneController c in controllers)
        {
            if (c.gameObject.scene != _scene)
                continue;

            if (found != null)
            {
                Debug.LogWarning($"[SceneController] '{_scene.name}' has more than one SceneController; using '{found.name}', ignoring '{c.name}'.", c);
                continue;
            }
            found = c;
        }
        return found;
    }

    #endregion

    #region CAMERA

    
    public void ApplyCameraSettings()
    {
        if (cameraSettingApplied) return;

        StagingConfig staging = AppManager.Instance.staging;
        displayCamera.sensorSize = new Vector2(staging.cropFactor, displayCamera.sensorSize.y);
        displayCamera.lensShift = new Vector2(staging.shiftX, staging.shiftY);

        cameraSettingApplied = true;
    }



    #endregion

    #region VIDEO EVENTS

    private void OnPrepared(VideoPlayer _player)
    {
        video.prepareCompleted -= OnPrepared;

        if (StartFrame <= 0)
        {
            IsReady = true;
            return;
        }

        // Skip ahead now, while the screen is still covered, so Play() doesn't have to seek.
        video.seekCompleted -= OnSeeked;
        video.seekCompleted += OnSeeked;
        video.frame = StartFrame;
    }

    private void OnSeeked(VideoPlayer _player)
    {
        video.seekCompleted -= OnSeeked;
        IsReady = true;
    }

    #endregion
}
