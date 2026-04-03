using System;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// Native platform implementation of <see cref="IAppMachinaPlatform"/>.
    /// Delegates to the Rust core via C ABI P/Invoke (<see cref="NativeBindings"/>).
    ///
    /// Used on iOS, Android, macOS, Windows, and Linux — any platform where the
    /// Rust core is compiled as a native library (.dylib/.so/.dll/.a).
    /// </summary>
    internal class NativePlatform : IAppMachinaPlatform
    {
        public string Init(string configJson)
        {
            return NativeStringHelper.ProcessResult(NativeBindings.appmachina_init(configJson));
        }

        public string Shutdown()
        {
            return NativeStringHelper.ProcessResult(NativeBindings.appmachina_shutdown());
        }

        public string Track(string eventName, string propertiesJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_track(eventName, propertiesJson));
        }

        public string Screen(string screenName, string propertiesJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_screen(screenName, propertiesJson));
        }

        public string Identify(string userId)
        {
            return NativeStringHelper.ProcessResult(NativeBindings.appmachina_identify(userId));
        }

        public string SetUserProperties(string propertiesJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_set_user_properties(propertiesJson));
        }

        public string SetUserPropertiesOnce(string propertiesJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_set_user_properties_once(propertiesJson));
        }

        public string Group(string groupId, string propertiesJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_group(groupId, propertiesJson));
        }

        public string SetConsent(string consentJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_set_consent(consentJson));
        }

        public string SetDeviceContext(string contextJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_set_device_context(contextJson));
        }

        public string Flush()
        {
            return NativeStringHelper.ProcessResult(NativeBindings.appmachina_flush());
        }

        public string DrainBatch(uint count)
        {
            return NativeStringHelper.ReadAndFree(NativeBindings.appmachina_drain_batch(count));
        }

        public string RequeueEvents(string eventsJson)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_requeue_events(eventsJson));
        }

        public string FlushHeadersJson()
        {
            return NativeStringHelper.ReadAndFree(NativeBindings.appmachina_flush_headers_json());
        }

        public string EventsUrl()
        {
            return NativeStringHelper.ReadAndFree(NativeBindings.appmachina_events_url());
        }

        public int QueueDepth()
        {
            return NativeBindings.appmachina_queue_depth();
        }

        public string GetSessionId()
        {
            return NativeStringHelper.ReadAndFree(NativeBindings.appmachina_get_session_id());
        }

        public string GetRemoteConfigJson()
        {
            return NativeStringHelper.ReadAndFree(NativeBindings.appmachina_get_remote_config_json());
        }

        public string UpdateRemoteConfig(string configJson, string etag)
        {
            return NativeStringHelper.ProcessResult(
                NativeBindings.appmachina_update_remote_config(configJson, etag));
        }
    }
}
