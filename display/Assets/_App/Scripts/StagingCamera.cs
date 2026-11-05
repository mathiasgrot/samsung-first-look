using UnityEngine;

/// <summary>
/// Put this on a scene's physical camera (e.g. DisplayCamera) to align it to the venue.
/// Applies AppManager's StagingConfig (StagingConfig.json): sensor width and lens shift.
/// The scene's SceneController calls Apply() when the scene is loaded.
/// </summary>
[RequireComponent(typeof(Camera))]
public class StagingCamera : MonoBehaviour
{
    #region VARIABLES

    private Camera cam;

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.usePhysicalProperties = true;
    }

    #endregion

    #region STAGING

    public void Apply()
    {
        StagingConfig staging = AppManager.Instance.staging;
        cam.sensorSize = new Vector2(staging.sensorSizeX, cam.sensorSize.y);
        cam.lensShift = new Vector2(staging.shiftX, staging.shiftY);
    }

    #endregion
}
