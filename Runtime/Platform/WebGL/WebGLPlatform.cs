using System;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// WebGL platform implementation of <see cref="IAppMachinaPlatform"/>.
    /// Delegates to the JavaScript bridge via <see cref="WebGLBindings"/>,
    /// which in turn calls the Rust WASM core.
    ///
    /// On WebGL, the Rust core is compiled to WASM and loaded as a separate
    /// module by the jslib. HTTP delivery uses browser <c>fetch()</c> and
    /// <c>navigator.sendBeacon()</c> instead of <c>UnityWebRequest</c>.
    ///
    /// Key differences from <see cref="NativePlatform"/>:
    /// - Strings are allocated via Emscripten's <c>_malloc</c>, freed via
    ///   <see cref="System.Runtime.InteropServices.Marshal.FreeHGlobal"/>
    /// - The jslib manages its own flush timer and lifecycle listeners
    /// - No Coroutine-based flush needed (jslib handles async fetch internally)
    /// - CAPI properties (cookies, page URL) are available via browser APIs
    /// </summary>
#if UNITY_WEBGL && !UNITY_EDITOR
    internal class WebGLPlatform : IAppMachinaPlatform
    {
        public string Init(string configJson)
        {
            // WebGL init is fire-and-forget from C# side.
            // The jslib handles WASM loading asynchronously and manages its own
            // flush timer and lifecycle listeners.
            WebGLBindings.AppMachinaWebGL_Init(configJson);
            return null; // Success — errors are logged to the JS console
        }

        public string Shutdown()
        {
            WebGLBindings.AppMachinaWebGL_Shutdown();
            return null;
        }

        public string Track(string eventName, string propertiesJson)
        {
            WebGLBindings.AppMachinaWebGL_Track(eventName, propertiesJson);
            return null;
        }

        public string Screen(string screenName, string propertiesJson)
        {
            WebGLBindings.AppMachinaWebGL_Screen(screenName, propertiesJson);
            return null;
        }

        public string Identify(string userId)
        {
            WebGLBindings.AppMachinaWebGL_Identify(userId);
            return null;
        }

        public string SetUserProperties(string propertiesJson)
        {
            WebGLBindings.AppMachinaWebGL_SetUserProperties(propertiesJson);
            return null;
        }

        public string SetUserPropertiesOnce(string propertiesJson)
        {
            WebGLBindings.AppMachinaWebGL_SetUserPropertiesOnce(propertiesJson);
            return null;
        }

        public string Group(string groupId, string propertiesJson)
        {
            WebGLBindings.AppMachinaWebGL_Group(groupId, propertiesJson);
            return null;
        }

        public string SetConsent(string consentJson)
        {
            WebGLBindings.AppMachinaWebGL_SetConsent(consentJson);
            return null;
        }

        public string SetDeviceContext(string contextJson)
        {
            WebGLBindings.AppMachinaWebGL_SetDeviceContext(contextJson);
            return null;
        }

        public string Flush()
        {
            WebGLBindings.AppMachinaWebGL_Flush();
            return null;
        }

        public string DrainBatch(uint count)
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_DrainBatch(count));
        }

        public string RequeueEvents(string eventsJson)
        {
            WebGLBindings.AppMachinaWebGL_RequeueEvents(eventsJson);
            return null;
        }

        public string FlushHeadersJson()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_FlushHeaders());
        }

        public string EventsUrl()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_EventsUrl());
        }

        public int QueueDepth()
        {
            return WebGLBindings.AppMachinaWebGL_QueueDepth();
        }

        public string GetSessionId()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetSessionId());
        }

        public string GetRemoteConfigJson()
        {
            return WebGLStringHelper.ReadAndFree(
                WebGLBindings.AppMachinaWebGL_GetRemoteConfigJson());
        }

        public string UpdateRemoteConfig(string configJson, string etag)
        {
            WebGLBindings.AppMachinaWebGL_UpdateRemoteConfig(configJson, etag);
            return null;
        }

        // ── WebGL-specific CAPI Properties ─────────────────────────────

        /// <summary>
        /// Get Meta's _fbp cookie value, or null if unavailable.
        /// </summary>
        public string GetFbpCookie()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetFbpCookie());
        }

        /// <summary>
        /// Get TikTok's _ttp cookie value, or null if unavailable.
        /// </summary>
        public string GetTtpCookie()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetTtpCookie());
        }

        /// <summary>
        /// Get the current page URL, or null if unavailable.
        /// </summary>
        public string GetPageUrl()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetPageUrl());
        }

        /// <summary>
        /// Get the fbc cookie/parameter value (Meta CAPI), or null if unavailable.
        /// </summary>
        public string GetFbc()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetFbc());
        }

        /// <summary>
        /// Get URL attribution parameters as a JSON string, or null if none found.
        /// Includes: fbclid, gclid, gbraid, wbraid, ttclid, msclkid, rclid,
        /// utm_source, utm_medium, utm_campaign, utm_content, utm_term, referrer_url.
        /// </summary>
        public string GetUrlParameters()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetUrlParameters());
        }

        /// <summary>
        /// Check if the browser is online.
        /// </summary>
        public bool IsOnline()
        {
            return WebGLBindings.AppMachinaWebGL_IsOnline() != 0;
        }

        // ── localStorage Persistence ───────────────────────────────────

        /// <summary>
        /// Set a key-value pair in localStorage.
        /// </summary>
        public void SetItem(string key, string value)
        {
            WebGLBindings.AppMachinaWebGL_SetItem(key, value);
        }

        /// <summary>
        /// Get a value from localStorage, or null if not found.
        /// </summary>
        public string GetItem(string key)
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetItem(key));
        }

        /// <summary>
        /// Remove a key from localStorage.
        /// </summary>
        public void RemoveItem(string key)
        {
            WebGLBindings.AppMachinaWebGL_RemoveItem(key);
        }

        // ── Browser Info ───────────────────────────────────────────────

        /// <summary>
        /// Get the browser's user agent string.
        /// </summary>
        public string GetUserAgent()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetUserAgent());
        }

        /// <summary>
        /// Get the browser's language (e.g. "en-US").
        /// </summary>
        public string GetLanguage()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetLanguage());
        }

        /// <summary>
        /// Get the screen size (e.g. "1920x1080").
        /// </summary>
        public string GetScreenSize()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetScreenSize());
        }

        /// <summary>
        /// Get the browser's timezone (e.g. "America/New_York").
        /// </summary>
        public string GetTimezone()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetTimezone());
        }

        /// <summary>
        /// Get the platform OS name from the browser's user agent.
        /// </summary>
        public string GetPlatformOS()
        {
            return WebGLStringHelper.ReadAndFree(WebGLBindings.AppMachinaWebGL_GetPlatformOS());
        }
    }
#endif
}
