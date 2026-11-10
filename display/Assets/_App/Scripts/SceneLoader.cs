using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Switches between the Idle, A and B scenes behind the transition animation.
/// Lives under AppManager (DontDestroyOnLoad), so the transition stays on screen while scenes swap.
///
/// Every load goes the same way:
///   "in" -> loop state (screen covered) -> swap scene -> reset scene, wait until ready
///   -> SceneLoaded -> wait for PlayScene() -> play scene + "out"
///
/// TouchDesignerBridge decides what loads and when it plays (TD owns the show).
/// PlayScene() may come early: it's kept until the loaded scene is ready.
///
/// The new scene loads in the background while "in" plays and replaces the old one additively,
/// which avoids the hitch of a Single load (UnloadUnusedAssets + GC in one frame).
/// A request made mid-transition loads once the current transition has finished.
/// </summary>

public class SceneLoader : MonoBehaviour
{
    #region VARIABLES

    public static SceneLoader Instance { get; private set; }

    [SerializeField] private string idleScene = "Idle";
    [SerializeField] private string aScene = "A";
    [SerializeField] private string bScene = "B";

    [Header("Transition")]
    [SerializeField] private Animator transitionAnimator;
    [Tooltip("Looping state the animator sits in while the screen is covered.")]
    [SerializeField] private string loopStateName = "loop";
    [Tooltip("State the animator returns to after 'out' has finished.")]
    [SerializeField] private string idleStateName = "idle";
    [Tooltip("Max seconds to wait for each step (animator state, scene ready, PlayScene) before moving on anyway.")]
    [SerializeField] private float timeout = 15f;

    private static readonly int InTrigger = Animator.StringToHash("in");
    private static readonly int OutTrigger = Animator.StringToHash("out");

    public string CurrentScene { get; private set; }
    public string TargetScene => pendingScene ?? loadingScene ?? CurrentScene;  // where the loader ends up
    public bool IsTransitioning { get; private set; }
    public bool IsLoading { get; private set; }  // true from the start of a transition until SceneLoaded

    public string IdleScene => idleScene;
    public string AScene => aScene;
    public string BScene => bScene;

    private bool playRequested;   // PlayScene() was called for the scene being loaded
    private string loadingScene;  // scene of the running transition
    private string pendingScene;  // requested mid-transition, loads after it

    #endregion

    #region EVENTS

    // Fired once the new scene is reset and ready (screen still covered). It plays on PlayScene().
    public event Action<string> SceneLoaded;

    #endregion

    #region LIFECYCLE

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            enabled = false;  // keeps Start from running before the destroy lands
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Background loads take less main-thread time per frame, so the animation stays smooth.
        Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Low;
    }

    // The first scene has no transition and TD starts in IDLE, so it plays as soon as it's ready.
    void Start()
    {
        LoadIdle();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    #endregion

    #region DEBUGGING

    void Update()
    {
        
        if (Input.GetKeyDown(KeyCode.Alpha1))  LoadA();
        else if (Input.GetKeyDown(KeyCode.Alpha2)) LoadB();
        else if (Input.GetKeyDown(KeyCode.Alpha3)) LoadIdle();
    }

    public void LoadA() => TouchDesignerBridge.Instance.RequestScene(SceneLoader.Instance.AScene);
    public void LoadB() => TouchDesignerBridge.Instance.RequestScene(SceneLoader.Instance.BScene);
    public void LoadIdle() => TouchDesignerBridge.Instance.RequestScene(SceneLoader.Instance.IdleScene);

    #endregion


    #region LOADING

    // Transitions to a scene. Does nothing if the loader already ends up there.
    public void LoadScene(string _sceneName)
    {
        if (_sceneName == TargetScene) return;

        if (IsTransitioning) pendingScene = _sceneName;
        else StartTransition(_sceneName);
    }

    // Starts the loaded scene and the "out" animation. Called by TouchDesignerBridge.
    public void PlayScene() => playRequested = true;

    #endregion

    #region TRANSITION

    private void StartTransition(string _sceneName)
    {
        if (!Application.CanStreamedLevelBeLoaded(_sceneName))
        {
            Debug.LogError($"[SceneLoader] Scene '{_sceneName}' can't be loaded. Is it in Build Settings?", this);
            return;
        }

        StartCoroutine(Transition(_sceneName));
    }

    private IEnumerator Transition(string _sceneName)
    {
        IsTransitioning = true;
        IsLoading = true;
        loadingScene = _sceneName;
        playRequested = false;

        // Load in the background while "in" covers the screen.
        Scene oldScene = SceneManager.GetActiveScene();
        AsyncOperation load = SceneManager.LoadSceneAsync(_sceneName, LoadSceneMode.Additive);
        load.allowSceneActivation = false;

        transitionAnimator.SetTrigger(InTrigger);
        yield return WaitFor(() => InState(loopStateName), $"animator state '{loopStateName}'");

        load.allowSceneActivation = true;
        yield return load;
        
        Scene newScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        SceneManager.SetActiveScene(newScene);
        yield return SceneManager.UnloadSceneAsync(oldScene);
        
        CurrentScene = _sceneName;

        // Reset the new scene, then wait to be told to play (TD revealing it).
        SceneController controller = SceneController.InScene(newScene);
        yield return ResetScene(controller);

        IsLoading = false;
        SceneLoaded?.Invoke(_sceneName);
        yield return WaitFor(() => playRequested, "PlayScene()");

        if (controller != null)
            controller.Play();
        transitionAnimator.SetTrigger(OutTrigger);
        yield return WaitFor(() => InState(idleStateName), $"animator state '{idleStateName}'");

        loadingScene = null;
        IsTransitioning = false;

        // A request that came in meanwhile.
        if (pendingScene != null)
        {
            string next = pendingScene;
            pendingScene = null;
            LoadScene(next);
        }
    }

    #endregion

    #region HELPERS

    private IEnumerator WaitFor(Func<bool> _done, string _what)
    {
        float start = Time.unscaledTime;
        while (!_done())
        {
            if (Time.unscaledTime - start > timeout)
            {
                Debug.LogWarning($"[SceneLoader] Gave up waiting for {_what} after {timeout}s.", this);
                yield break;
            }
            yield return null;
        }
    }

    private bool InState(string _stateName)
    {
        return !transitionAnimator.IsInTransition(0)
            && transitionAnimator.GetCurrentAnimatorStateInfo(0).IsName(_stateName);
    }

    // Resets the scene's controller and waits until it's ready. Scenes without one skip this.
    private IEnumerator ResetScene(SceneController _controller)
    {
        if (_controller == null)
            yield break;

        _controller.ResetScene();
        yield return WaitFor(() => _controller.IsReady, "scene to be ready");
    }

    #endregion
}
