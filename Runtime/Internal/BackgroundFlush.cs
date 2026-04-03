// BackgroundFlush.cs
// AppMachina Unity SDK
//
// Background flush support using platform-specific background task APIs:
// - iOS: BGAppRefreshTask with identifier "com.appmachina.sdk.background-flush"
// - Android: WorkManager periodic work with tag "com.appmachina.sdk.flush"
//
// The minimum interval is 15 minutes on both platforms (OS-enforced).
//
// iOS Setup:
//   1. Add "com.appmachina.sdk.background-flush" to Info.plist under
//      BGTaskSchedulerPermittedIdentifiers.
//   2. Call appmachina_background_flush_register() in
//      application:didFinishLaunchingWithOptions: (handled automatically
//      if using the AppMachinaBackgroundFlush.mm native plugin).
//
// Android Setup:
//   No additional setup required. WorkManager is called via JNI.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// Background flush controller. Schedules platform-specific background tasks
    /// to flush queued events when the app is not in the foreground.
    ///
    /// Toggle via <see cref="AppMachinaSDK.EnableBackgroundFlush"/> and
    /// <see cref="AppMachinaSDK.DisableBackgroundFlush"/>.
    /// </summary>
    internal class BackgroundFlush : MonoBehaviour
    {
        private static bool _enabled;

        /// <summary>Whether background flush is currently enabled.</summary>
        internal static bool IsEnabled => _enabled;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern bool appmachina_background_flush_enable();

        [DllImport("__Internal")]
        private static extern void appmachina_background_flush_disable();

        [DllImport("__Internal")]
        private static extern void appmachina_background_flush_completed();
#endif

        /// <summary>
        /// Enable periodic background flush.
        ///
        /// On iOS, schedules a <c>BGAppRefreshTask</c>.
        /// On Android, enqueues a periodic WorkManager job.
        ///
        /// Returns <c>true</c> if scheduling succeeded.
        /// </summary>
        internal static bool Enable()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                bool result = appmachina_background_flush_enable();
                _enabled = result;
                if (_enabled)
                    AppMachinaLogger.Log("Background flush enabled (iOS BGAppRefreshTask)");
                else
                    AppMachinaLogger.Warn("Background flush scheduling failed on iOS");
                return _enabled;
            }
            catch (Exception e)
            {
                AppMachinaLogger.Warn($"Background flush enable failed: {e.Message}");
                return false;
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            return EnableAndroid();
#else
            AppMachinaLogger.Log("Background flush not available in Editor");
            return false;
#endif
        }

        /// <summary>
        /// Disable periodic background flush.
        /// Safe to call even if background flush was never enabled.
        /// </summary>
        internal static void Disable()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                appmachina_background_flush_disable();
            }
            catch (Exception e)
            {
                AppMachinaLogger.Warn($"Background flush disable failed: {e.Message}");
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            DisableAndroid();
#endif
            _enabled = false;
            AppMachinaLogger.Log("Background flush disabled");
        }

        // ── iOS Callback ─────────────────────────────────────────────────

        /// <summary>
        /// Called by the native iOS plugin via UnitySendMessage when a
        /// background task fires. The message is sent to a GameObject named
        /// "_AppMachinaBackgroundFlush".
        /// </summary>
        // ReSharper disable once UnusedMember.Local -- called via UnitySendMessage
        private void OnBackgroundFlush(string message)
        {
            if (AppMachinaSDK.IsInitialized)
            {
                AppMachinaLogger.Log("Background flush triggered (iOS)");

                // Use callback-based flush so we only signal completion AFTER
                // the HTTP request finishes. Without this, iOS may suspend
                // the app before events are actually delivered.
                AppMachinaSDK.FlushWithCallback(() =>
                {
#if UNITY_IOS && !UNITY_EDITOR
                    try
                    {
                        appmachina_background_flush_completed();
                    }
                    catch (Exception e)
                    {
                        AppMachinaLogger.Warn($"Background flush completion signal failed: {e.Message}");
                    }
#endif
                });
            }
            else
            {
                // SDK not initialized — signal completion immediately so iOS
                // doesn't wait indefinitely.
#if UNITY_IOS && !UNITY_EDITOR
                try
                {
                    appmachina_background_flush_completed();
                }
                catch (Exception e)
                {
                    AppMachinaLogger.Warn($"Background flush completion signal failed: {e.Message}");
                }
#endif
            }
        }

        // ── Android WorkManager ──────────────────────────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string WorkTag = "com.appmachina.sdk.flush";
        private const long MinIntervalMs = 15 * 60 * 1000; // 15 minutes

        private static bool EnableAndroid()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                {
                    // TimeUnit.MILLISECONDS
                    using (var timeUnitClass = new AndroidJavaClass("java.util.concurrent.TimeUnit"))
                    using (var milliseconds = timeUnitClass.GetStatic<AndroidJavaObject>("MILLISECONDS"))
                    {
                        // Build PeriodicWorkRequest
                        using (var builderClass = new AndroidJavaObject(
                            "androidx.work.PeriodicWorkRequest$Builder",
                            new AndroidJavaClass("com.appmachina.sdk.unity.AppMachinaFlushWorker"),
                            MinIntervalMs,
                            milliseconds))
                        {
                            using (var workRequest = builderClass.Call<AndroidJavaObject>("build"))
                            using (var workManagerClass = new AndroidJavaClass("androidx.work.WorkManager"))
                            using (var workManager = workManagerClass.CallStatic<AndroidJavaObject>(
                                "getInstance", context))
                            {
                                // ExistingPeriodicWorkPolicy.KEEP
                                using (var policyClass = new AndroidJavaClass(
                                    "androidx.work.ExistingPeriodicWorkPolicy"))
                                using (var keepPolicy = policyClass.GetStatic<AndroidJavaObject>("KEEP"))
                                {
                                    workManager.Call("enqueueUniquePeriodicWork",
                                        WorkTag, keepPolicy, workRequest);
                                }
                            }
                        }
                    }
                }

                _enabled = true;
                AppMachinaLogger.Log("Background flush enabled (Android WorkManager)");
                return true;
            }
            catch (Exception e)
            {
                // WorkManager may not be available (e.g., missing dependency)
                AppMachinaLogger.Warn($"Android background flush failed: {e.Message}");
                return false;
            }
        }

        private static void DisableAndroid()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var workManagerClass = new AndroidJavaClass("androidx.work.WorkManager"))
                using (var workManager = workManagerClass.CallStatic<AndroidJavaObject>(
                    "getInstance", context))
                {
                    workManager.Call("cancelUniqueWork", WorkTag);
                }
            }
            catch (Exception e)
            {
                AppMachinaLogger.Warn($"Android background flush cancel failed: {e.Message}");
            }
        }
#endif

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Create the receiver GameObject for iOS UnitySendMessage callbacks.
        /// Must be called during SDK initialization.
        /// </summary>
        internal static void EnsureReceiverExists()
        {
#if UNITY_IOS && !UNITY_EDITOR
            // The native plugin sends messages to a GameObject named "_AppMachinaBackgroundFlush".
            // Create it if it doesn't exist.
            var existing = GameObject.Find("_AppMachinaBackgroundFlush");
            if (existing == null)
            {
                var go = new GameObject("_AppMachinaBackgroundFlush");
                go.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(go);
                go.AddComponent<BackgroundFlush>();
            }
#endif
        }
    }
}
