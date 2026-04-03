using System;
using System.Runtime.InteropServices;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// P/Invoke declarations for the WebGL JavaScript bridge (AppMachinaWebGL.jslib).
    /// These map 1:1 to the exported functions in the jslib.
    ///
    /// On WebGL, Unity compiles C# to WASM via IL2CPP. The Rust WASM core is a
    /// separate module loaded by the jslib. This class provides the C# → jslib
    /// bridge via [DllImport("__Internal")].
    ///
    /// String returns: the jslib allocates C strings on the Unity heap via _malloc.
    /// The caller MUST free them via Marshal.FreeHGlobal(). Use
    /// <see cref="WebGLStringHelper"/> for safe read-and-free patterns.
    ///
    /// Only compiled for UNITY_WEBGL && !UNITY_EDITOR to avoid link errors on
    /// other platforms.
    /// </summary>
#if UNITY_WEBGL && !UNITY_EDITOR
    internal static class WebGLBindings
    {
        // ── Lifecycle ──────────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Init(string configJson);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Shutdown();

        // ── Event Tracking ─────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Track(string eventName, string propertiesJson);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Screen(string screenName, string propertiesJson);

        // ── User Identity ──────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Identify(string userId);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Group(string groupId, string propertiesJson);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_SetUserProperties(string propertiesJson);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_SetUserPropertiesOnce(string propertiesJson);

        // ── Consent ────────────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_SetConsent(string consentJson);

        // ── Device Context ─────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_SetDeviceContext(string contextJson);

        // ── Flush / Drain ──────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_Flush();

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_FlushBlocking();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_DrainBatch(uint count);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_RequeueEvents(string eventsJson);

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_FlushHeaders();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_EventsUrl();

        // ── Queue State ────────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern int AppMachinaWebGL_QueueDepth();

        [DllImport("__Internal")]
        internal static extern int AppMachinaWebGL_IsInitialized();

        // ── Session ────────────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetSessionId();

        // ── Remote Config ──────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetRemoteConfigJson();

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_UpdateRemoteConfig(
            string configJson, string etag);

        // ── CAPI Properties ────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetFbpCookie();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetTtpCookie();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetPageUrl();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetFbc();

        // ── Attribution ────────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetUrlParameters();

        // ── Online/Offline ─────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern int AppMachinaWebGL_IsOnline();

        // ── localStorage Persistence ───────────────────────────────────

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_SetItem(string key, string value);

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetItem(string key);

        [DllImport("__Internal")]
        internal static extern void AppMachinaWebGL_RemoveItem(string key);

        // ── Browser Info ───────────────────────────────────────────────

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetUserAgent();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetLanguage();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetScreenSize();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetTimezone();

        [DllImport("__Internal")]
        internal static extern IntPtr AppMachinaWebGL_GetPlatformOS();
    }
#endif
}
