using UnityEngine;
using System.Collections.Generic;
using AppMachina.Unity;

/// <summary>
/// Sample MonoBehaviour demonstrating basic usage of the AppMachina Unity SDK.
///
/// Attach this script to a GameObject in your scene to see the SDK in action.
/// Set the App ID in the Inspector or directly in the field below.
///
/// This sample covers:
/// - SDK initialization with configuration
/// - Error handling via OnError event
/// - Deep link listener registration
/// - Custom event tracking with properties
/// - Screen view tracking
/// - User identification
/// - User properties (set and set-once)
/// - Consent management
/// - ATT request (iOS only, no-op on other platforms)
/// - Graceful shutdown
/// </summary>
public class AppMachinaSample : MonoBehaviour
{
    [Header("AppMachina SDK Configuration")]
    [Tooltip("Your app ID from the AppMachina dashboard")]
    [SerializeField] private string appId = "your-app-id";

    [Tooltip("Override the ingest URL for local testing (e.g. http://localhost:3333)")]
    [SerializeField] private string baseUrl = "";

    [Tooltip("Enable verbose SDK logging")]
    [SerializeField] private bool enableDebug = true;

    void Start()
    {
        // ── Initialize the SDK ──────────────────────────────────────
        var config = new AppMachinaConfig
        {
            AppId = appId,
            Environment = AppMachinaEnvironment.Development,
            EnableDebug = enableDebug,
            AutoTrackAppOpen = true,
            AutoTrackDeepLinks = true
        };

        // Point to mock server for local testing
        if (!string.IsNullOrEmpty(baseUrl))
            config.BaseUrl = baseUrl;

        AppMachinaSDK.Initialize(config);

        // ── Error handling ──────────────────────────────────────────
        AppMachinaSDK.OnError += (method, error) =>
            Debug.LogWarning($"[AppMachinaSample] AppMachina error in {method}: {error}");

        // ── Deep link listener ──────────────────────────────────────
        DeepLinksModule.OnDeepLinkReceived += (data) =>
        {
            Debug.Log($"[AppMachinaSample] Deep link received: {data.RawUrl}");
            Debug.Log($"[AppMachinaSample]   Scheme: {data.Scheme}, Host: {data.Host}, Path: {data.Path}");

            if (data.Attribution != null)
            {
                if (!string.IsNullOrEmpty(data.Attribution.UtmSource))
                    Debug.Log($"[AppMachinaSample]   UTM Source: {data.Attribution.UtmSource}");
                if (!string.IsNullOrEmpty(data.Attribution.UtmCampaign))
                    Debug.Log($"[AppMachinaSample]   UTM Campaign: {data.Attribution.UtmCampaign}");
            }
        };

        // ── Track a custom event ────────────────────────────────────
        AppMachinaSDK.Track("game_started", new Dictionary<string, object>
        {
            ["level"] = 1,
            ["difficulty"] = "normal",
            ["tutorial_complete"] = false
        });

        // ── Track a screen view ─────────────────────────────────────
        AppMachinaSDK.Screen("MainMenu");

        // ── Identify user ───────────────────────────────────────────
        AppMachinaSDK.Identify("user-123");

        // ── Set user properties ─────────────────────────────────────
        AppMachinaSDK.SetUserProperties(new Dictionary<string, object>
        {
            ["plan"] = "premium",
            ["signup_date"] = "2024-01-15",
            ["level"] = 42
        });

        // ── Set user properties once (only set if not already set) ──
        AppMachinaSDK.SetUserPropertiesOnce(new Dictionary<string, object>
        {
            ["first_seen"] = System.DateTime.UtcNow.ToString("o"),
            ["initial_platform"] = "unity"
        });

        // ── Set consent ─────────────────────────────────────────────
        AppMachinaSDK.SetConsent(analytics: true, advertising: false);

        // ── Request ATT (iOS only - no-op on Android/Editor) ────────
        ATTModule.RequestTracking((status) =>
        {
            Debug.Log($"[AppMachinaSample] ATT status: {status}");
            if (status == AppMachinaATTStatus.Authorized)
            {
                AppMachinaSDK.SetConsent(advertising: true);
                Debug.Log("[AppMachinaSample] Advertising consent granted via ATT");
            }
        });

        Debug.Log("[AppMachinaSample] SDK initialized and sample events tracked");
    }

    /// <summary>
    /// Example: track an in-game purchase event.
    /// Call this from a UI button or game logic.
    /// </summary>
    public void TrackPurchase(string itemId, double price, string currency)
    {
        AppMachinaSDK.Track("purchase", new Dictionary<string, object>
        {
            ["item_id"] = itemId,
            ["price"] = price,
            ["currency"] = currency
        });
    }

    /// <summary>
    /// Example: track a level completion event.
    /// </summary>
    public void TrackLevelComplete(int level, float timeSeconds)
    {
        AppMachinaSDK.Track("level_complete", new Dictionary<string, object>
        {
            ["level"] = level,
            ["time_seconds"] = timeSeconds,
            ["user_id"] = AppMachinaSDK.UserId
        });
    }

    /// <summary>
    /// Example: manually flush events (e.g. before a loading screen).
    /// </summary>
    public void FlushEvents()
    {
        AppMachinaSDK.Flush();
    }

    /// <summary>
    /// Example: reset user state on logout.
    /// </summary>
    public void OnLogout()
    {
        AppMachinaSDK.Reset();
        Debug.Log("[AppMachinaSample] User state reset");
    }

    void OnDestroy()
    {
        AppMachinaSDK.Shutdown();
    }
}
