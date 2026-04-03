// ATTModule.cs
// AppMachina Unity SDK
//
// C# wrapper for App Tracking Transparency (ATT) on iOS.
// Delegates to native Objective-C bridge via P/Invoke.
// On non-iOS platforms, all methods return safe defaults.

using System;
using System.Runtime.InteropServices;
using AOT;

namespace AppMachina.Unity
{
    /// <summary>
    /// ATT authorization status values matching Apple's ATTrackingManager.AuthorizationStatus.
    /// </summary>
    public enum AppMachinaATTStatus
    {
        /// <summary>User has not yet been prompted.</summary>
        NotDetermined = 0,

        /// <summary>Authorization restricted by device policy (e.g., parental controls).</summary>
        Restricted = 1,

        /// <summary>User denied tracking authorization.</summary>
        Denied = 2,

        /// <summary>User authorized tracking.</summary>
        Authorized = 3
    }

    /// <summary>
    /// App Tracking Transparency module for iOS.
    /// Provides access to ATTrackingManager for IDFA consent, advertising identifier,
    /// and vendor identifier retrieval.
    /// On non-iOS platforms (Android, Editor), all methods return safe no-op defaults.
    /// </summary>
    public static class ATTModule
    {
        // Delegate type matching the native callback signature.
        private delegate void ATTCallbackDelegate(int status);

        private static Action<AppMachinaATTStatus> _pendingCallback;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern bool appmachina_att_is_available();

        [DllImport("__Internal")]
        private static extern int appmachina_att_get_status();

        [DllImport("__Internal")]
        private static extern void appmachina_att_request_tracking(ATTCallbackDelegate callback);

        [DllImport("__Internal")]
        private static extern IntPtr appmachina_att_get_idfa();

        [DllImport("__Internal")]
        private static extern IntPtr appmachina_att_get_idfv();
#endif

        /// <summary>
        /// Check if ATT is available on this device (iOS 14.0+).
        /// </summary>
        /// <returns>True if ATT APIs are available, false otherwise.</returns>
        public static bool IsAvailable()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return appmachina_att_is_available();
#else
            return false;
#endif
        }

        /// <summary>
        /// Get the current ATT authorization status without prompting the user.
        /// </summary>
        /// <returns>The current authorization status.</returns>
        public static AppMachinaATTStatus GetStatus()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return (AppMachinaATTStatus)appmachina_att_get_status();
#else
            return AppMachinaATTStatus.NotDetermined;
#endif
        }

        /// <summary>
        /// Request tracking authorization from the user.
        /// Shows the system ATT dialog if the user has not yet been prompted.
        /// If the user has already responded, returns the existing status without showing the dialog.
        /// The callback is invoked on the main thread.
        /// </summary>
        /// <param name="callback">Called with the resulting AppMachinaATTStatus when the user responds or if already determined.</param>
        public static void RequestTracking(Action<AppMachinaATTStatus> callback)
        {
#if UNITY_IOS && !UNITY_EDITOR
            _pendingCallback = callback;
            appmachina_att_request_tracking(OnNativeTrackingResult);
#else
            callback?.Invoke(AppMachinaATTStatus.NotDetermined);
#endif
        }

        /// <summary>
        /// Get the IDFA (Identifier for Advertisers).
        /// Returns null if ATT is not authorized, not available, or the IDFA is zeroed out.
        /// </summary>
        /// <returns>The IDFA string, or null if unavailable.</returns>
        public static string GetAdvertisingId()
        {
#if UNITY_IOS && !UNITY_EDITOR
            IntPtr ptr = appmachina_att_get_idfa();
            if (ptr == IntPtr.Zero)
                return null;

            string idfa = Marshal.PtrToStringAnsi(ptr);
            // Native side returns empty string when not authorized or zeroed.
            if (string.IsNullOrEmpty(idfa))
                return null;

            return idfa;
#else
            return null;
#endif
        }

        /// <summary>
        /// Get the IDFV (Identifier for Vendor).
        /// Always available on iOS, does not require ATT authorization.
        /// </summary>
        /// <returns>The IDFV string, or null on non-iOS platforms.</returns>
        public static string GetVendorId()
        {
#if UNITY_IOS && !UNITY_EDITOR
            IntPtr ptr = appmachina_att_get_idfv();
            if (ptr == IntPtr.Zero)
                return null;

            string idfv = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(idfv))
                return null;

            return idfv;
#else
            return null;
#endif
        }

        /// <summary>
        /// Check if the user has already been prompted for tracking authorization.
        /// </summary>
        /// <returns>True if the status is anything other than NotDetermined.</returns>
        public static bool HasBeenPrompted()
        {
            return GetStatus() != AppMachinaATTStatus.NotDetermined;
        }

        // Native callback — must be static, decorated with MonoPInvokeCallback,
        // and match the delegate signature exactly.
        [MonoPInvokeCallback(typeof(ATTCallbackDelegate))]
        private static void OnNativeTrackingResult(int status)
        {
            var attStatus = (AppMachinaATTStatus)status;
            var callback = _pendingCallback;
            _pendingCallback = null;
            callback?.Invoke(attStatus);
        }
    }
}
