using UnityEngine;

namespace AppMachina.Unity.Internal
{
    /// <summary>
    /// Simple Debug.Log wrapper gated on the enableDebug flag.
    /// All SDK internal logging goes through this class so it can be silenced in production.
    /// </summary>
    internal static class AppMachinaLogger
    {
        internal static bool Enabled { get; set; }

        internal static void Log(string message)
        {
            if (Enabled)
                Debug.Log($"[AppMachina] {message}");
        }

        internal static void Warn(string message)
        {
            if (Enabled)
                Debug.LogWarning($"[AppMachina] {message}");
        }

        internal static void Error(string message)
        {
            // Errors are always logged regardless of debug flag
            Debug.LogError($"[AppMachina] {message}");
        }
    }
}
