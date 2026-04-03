namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// Factory that selects the correct <see cref="IAppMachinaPlatform"/> implementation
    /// based on the build target:
    ///
    /// - WebGL: <see cref="WebGLPlatform"/> (jslib → Rust WASM)
    /// - Everything else: <see cref="NativePlatform"/> (P/Invoke → Rust native lib)
    ///
    /// Platform selection is compile-time via #if directives, so no runtime
    /// overhead or reflection is involved.
    /// </summary>
    internal static class AppMachinaPlatformFactory
    {
        internal static IAppMachinaPlatform Create()
        {
            // Test mode: return mock platform for unit testing without native lib
            if (AppMachinaTestMode.IsEnabled)
            {
                var mock = AppMachinaTestMode.GetMockPlatform();
                if (mock != null) return mock;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLPlatform();
#else
            return new NativePlatform();
#endif
        }
    }
}
