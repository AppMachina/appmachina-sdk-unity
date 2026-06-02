using UnityEngine;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// Hidden MonoBehaviour singleton that hosts coroutines for periodic flush and
    /// remote config polling, and forwards Unity lifecycle callbacks to the main
    /// <see cref="AppMachinaSDK"/> class.
    ///
    /// Created lazily on first access. The GameObject is marked with
    /// <see cref="HideFlags.HideAndDontSave"/> so it does not appear in the
    /// hierarchy and survives scene loads via <see cref="Object.DontDestroyOnLoad"/>.
    ///
    /// In addition to flush/lifecycle plumbing, this runner is responsible for
    /// the Tier 2 lifecycle auto-capture surface — <c>$app_open</c>,
    /// <c>$app_background</c>, <c>$app_terminate</c>, and the once-per-install
    /// <c>$first_open</c> / version-change <c>$app_update</c> events.
    /// </summary>
    internal class AppMachinaRunner : MonoBehaviour
    {
        private static AppMachinaRunner _instance;
        private NetworkReachability _lastReachability;

        // Tier 2: tracks whether we've already emitted an $app_open for the
        // current foreground session. Reset to false when entering background.
        private static bool _appOpenEmittedThisSession;

        internal static AppMachinaRunner Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[AppMachina]");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<AppMachinaRunner>();
                }
                return _instance;
            }
        }

        private void Awake()
        {
            // Capture the main thread ID so ExceptionModule's threaded-log
            // dedupe heuristic works correctly. Awake runs on the main thread.
            ExceptionModule.RefreshMainThreadId();
        }

        private void Start()
        {
            _lastReachability = Application.internetReachability;

            // Cold-launch is a foreground transition. OnApplicationPause(false)
            // does NOT fire on the first frame, so emit the initial $app_open
            // here. This mirrors the Swift/Kotlin SDKs which auto-fire on init.
            AppMachinaSDK.OnLifecycleColdLaunch();
            _appOpenEmittedThisSession = true;
        }

        private void Update()
        {
            var current = Application.internetReachability;
            if (_lastReachability == NetworkReachability.NotReachable
                && current != NetworkReachability.NotReachable)
            {
                // Went from offline to online — flush queued events
                AppMachinaSDK.OnReconnected();
            }
            _lastReachability = current;

            // Tier 5: drain background-thread exception queue on the main
            // thread. Cheap when empty (one ConcurrentQueue.IsEmpty check).
            ExceptionModule.DrainBackgroundQueue();

            // Tier 6: tick the periodic frame-timing sampler.
            PerformanceModule.Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Called by Unity when the app is paused (backgrounded) or resumed (foregrounded).
        /// On mobile platforms this fires when the app enters/exits the background.
        ///
        /// Tier 2 auto-capture: emits <c>$app_background</c> / <c>$app_open</c>.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _appOpenEmittedThisSession = false;
                AppMachinaSDK.OnBackgrounded();
            }
            else
            {
                AppMachinaSDK.OnForegrounded();
                if (!_appOpenEmittedThisSession)
                {
                    AppMachinaSDK.OnLifecycleResume();
                    _appOpenEmittedThisSession = true;
                }
            }
        }

        /// <summary>
        /// Called by Unity when the application is about to quit.
        /// Emits <c>$app_terminate</c>, then triggers a synchronous shutdown to
        /// persist queued events.
        /// </summary>
        private void OnApplicationQuit()
        {
            AppMachinaSDK.OnLifecycleTerminate();
            AppMachinaSDK.OnQuitting();
        }

        private void OnDestroy()
        {
            _instance = null;
            _appOpenEmittedThisSession = false;
        }
    }
}
