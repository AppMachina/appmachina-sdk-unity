// AppMachinaWebGL.jslib — JavaScript bridge between Unity C# (via IL2CPP WASM) and
// the AppMachina Rust WASM core. Unity WebGL builds compile C# to WASM via IL2CPP,
// but the Rust WASM binary is a separate module loaded via this bridge.
//
// Architecture:
//   C# → [DllImport("__Internal")] → this jslib → Rust WASM core
//   HTTP delivery: Rust WASM drain() → JS fetch() (same pattern as @appmachina/client)
//
// The Rust WASM core is the SINGLE SOURCE OF TRUTH for event acceptance, building,
// queuing, consent, rate limiting, sampling, and serialization. This bridge only
// handles: WASM loading, HTTP delivery (fetch/sendBeacon), browser APIs (cookies,
// localStorage, navigator), and lifecycle listeners.

var AppMachinaWebGLPlugin = {
  // ── Internal State ────────────────────────────────────────────────────

  $AppMachinaState: {
    wasm: null, // WasmAppMachinaCore instance (from Rust WASM)
    wasmModule: null, // Raw WASM module reference
    wasmReady: false,
    config: null, // Parsed config object
    baseUrl: 'https://in.appmachina.com',
    flushTimer: null,
    isFlushing: false,
    isShutDown: false,
    circuitFailures: 0,
    circuitState: 'closed', // 'closed' | 'open' | 'half-open'
    circuitOpenedAt: 0,
    circuitThreshold: 5,
    circuitResetMs: 60000,
    maxBatchSize: 20,

    // Retry-After gate (mirrors WASM/Rust behavior)
    retryAfterUntilMs: 0,
    RETRY_AFTER_MAX_SECS: 300,

    // Pre-init event queue: buffers calls made before WASM is ready.
    // Each entry is { method: string, args: array }.
    // Replayed in order once WASM init completes.
    preInitQueue: [],
    PRE_INIT_MAX: 1000,

    // Listeners for cleanup
    onlineListener: null,
    offlineListener: null,
    visibilityListener: null,
    beforeUnloadListener: null,

    // SPA deep link tracking
    popstateListener: null,
    hashchangeListener: null,
    lastDeepLinkUrl: null,
    lastDeepLinkTimestamp: 0,
    DEEP_LINK_DEDUP_MS: 2000
  },

  // ── WASM Loading ──────────────────────────────────────────────────────

  // Load the Rust WASM binary. Looks for it in StreamingAssets first,
  // then falls back to a relative path. Returns a promise.
  $AppMachinaLoadWasm: function () {
    return new Promise(function (resolve, reject) {
      // Try StreamingAssets path first (standard Unity WebGL pattern)
      var paths = [
        'StreamingAssets/appmachina_core_bg.wasm',
        'appmachina_core_bg.wasm',
        './appmachina_core_bg.wasm'
      ];

      function tryLoad(index) {
        if (index >= paths.length) {
          reject(new Error('Failed to load AppMachina WASM binary from any path'));
          return;
        }

        fetch(paths[index])
          .then(function (response) {
            if (!response.ok) {
              tryLoad(index + 1);
              return;
            }
            return response.arrayBuffer();
          })
          .then(function (buffer) {
            if (!buffer) return;
            return WebAssembly.instantiate(buffer);
          })
          .then(function (result) {
            if (!result) return;
            AppMachinaState.wasmModule = result.instance;
            AppMachinaState.wasmReady = true;
            resolve(result.instance);
          })
          .catch(function () {
            tryLoad(index + 1);
          });
      }

      tryLoad(0);
    });
  },

  // ── String Helpers ────────────────────────────────────────────────────

  // Allocate a C string on the Unity heap and return its pointer.
  // The caller (C#) is responsible for freeing via Marshal.FreeHGlobal.
  $AppMachinaAllocString: function (str) {
    if (str === null || str === undefined) return 0;
    var bufferSize = lengthBytesUTF8(str) + 1;
    var buffer = _malloc(bufferSize);
    stringToUTF8(str, buffer, bufferSize);
    return buffer;
  },

  // ── Circuit Breaker ───────────────────────────────────────────────────

  $AppMachinaCheckCircuit: function () {
    if (AppMachinaState.circuitState === 'open') {
      if (Date.now() - AppMachinaState.circuitOpenedAt >= AppMachinaState.circuitResetMs) {
        AppMachinaState.circuitState = 'half-open';
        return true;
      }
      return false;
    }
    return true;
  },

  $AppMachinaRecordSuccess: function () {
    AppMachinaState.circuitFailures = 0;
    if (AppMachinaState.circuitState === 'half-open') {
      AppMachinaState.circuitState = 'closed';
    }
  },

  $AppMachinaRecordFailure: function () {
    AppMachinaState.circuitFailures++;
    if (AppMachinaState.circuitFailures >= AppMachinaState.circuitThreshold) {
      AppMachinaState.circuitState = 'open';
      AppMachinaState.circuitOpenedAt = Date.now();
    }
  },

  // ── Retry-After Helpers ───────────────────────────────────────────────

  $AppMachinaUpdateRetryAfter: function (headerValue) {
    if (!headerValue) {
      AppMachinaState.retryAfterUntilMs = Date.now() + 60000;
      return;
    }
    var deltaSecs = parseInt(headerValue, 10);
    if (!isNaN(deltaSecs) && deltaSecs > 0) {
      var capped = Math.min(deltaSecs, AppMachinaState.RETRY_AFTER_MAX_SECS);
      AppMachinaState.retryAfterUntilMs = Date.now() + capped * 1000;
      return;
    }
    var dateMs = Date.parse(headerValue);
    if (!isNaN(dateMs)) {
      var delaySecs = Math.max(0, (dateMs - Date.now()) / 1000);
      var cappedSecs = Math.min(delaySecs, AppMachinaState.RETRY_AFTER_MAX_SECS);
      AppMachinaState.retryAfterUntilMs = Date.now() + cappedSecs * 1000;
      return;
    }
    AppMachinaState.retryAfterUntilMs = Date.now() + 60000;
  },

  $AppMachinaIsRetryAfterActive: function () {
    return AppMachinaState.retryAfterUntilMs > 0 && Date.now() < AppMachinaState.retryAfterUntilMs;
  },

  // ── HTTP Delivery ─────────────────────────────────────────────────────

  // Drain events from the Rust WASM core and send via fetch.
  // On failure, requeue events back into the Rust queue.
  // This mirrors the drain+fetch pattern from @appmachina/client.
  $AppMachinaFlushViaFetch: function () {
    if (AppMachinaState.isFlushing || AppMachinaState.isShutDown) return;
    if (!AppMachinaState.wasm) return;
    if (!AppMachinaCheckCircuit()) return;
    if (AppMachinaIsRetryAfterActive()) return;

    AppMachinaState.isFlushing = true;

    try {
      var batchJson = AppMachinaState.wasm.drain(AppMachinaState.maxBatchSize);
      if (batchJson === null || batchJson === undefined) {
        AppMachinaState.isFlushing = false;
        return;
      }

      var headers = AppMachinaState.wasm.flushHeaders();
      var url = AppMachinaState.wasm.eventsUrl();

      // Parse events for potential requeue
      var eventsJson = null;
      try {
        var parsed = JSON.parse(batchJson);
        eventsJson = JSON.stringify(parsed.events);
      } catch (e) {
        // Can't parse — won't be able to requeue
      }

      var headerObj = { 'Content-Type': 'application/json' };
      if (Array.isArray(headers)) {
        for (var i = 0; i < headers.length; i++) {
          headerObj[headers[i][0]] = headers[i][1];
        }
      }

      var maxRetries = 3;
      var attempt = 0;

      function doAttempt() {
        fetch(url, {
          method: 'POST',
          headers: headerObj,
          body: batchJson,
          keepalive: true
        })
          .then(function (response) {
            if (response.ok) {
              AppMachinaRecordSuccess();
              AppMachinaState.retryAfterUntilMs = 0;
              AppMachinaState.isFlushing = false;
              // Try to drain more if events remain
              if (AppMachinaState.wasm && AppMachinaState.wasm.queueDepth() > 0) {
                AppMachinaFlushViaFetch();
              }
              return;
            }

            if (response.status === 429 || response.status >= 500) {
              AppMachinaRecordFailure();
              var retryAfterHeader = response.headers.get('retry-after');
              AppMachinaUpdateRetryAfter(retryAfterHeader);

              attempt++;
              if (attempt < maxRetries) {
                var delay = Math.min(1000 * Math.pow(2, attempt) + Math.random() * 250, 30000);
                setTimeout(doAttempt, delay);
                return;
              }

              // Retries exhausted for retryable error — requeue for later
              if (eventsJson && AppMachinaState.wasm) {
                try {
                  AppMachinaState.wasm.requeue(eventsJson);
                } catch (e) {}
              }
            } else {
              // Non-retryable error (400, 401, 403, etc.) — drop events to
              // avoid an infinite requeue loop. Record failure so circuit
              // breaker can open if the server keeps rejecting requests.
              AppMachinaRecordFailure();
              if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
                console.warn(
                  '[AppMachina] Non-retryable HTTP ' + response.status + ', dropping batch'
                );
              }
            }

            AppMachinaState.isFlushing = false;
          })
          .catch(function () {
            AppMachinaRecordFailure();
            attempt++;
            if (attempt < maxRetries) {
              var delay = Math.min(1000 * Math.pow(2, attempt) + Math.random() * 250, 30000);
              setTimeout(doAttempt, delay);
              return;
            }
            // Retries exhausted — requeue
            if (eventsJson && AppMachinaState.wasm) {
              try {
                AppMachinaState.wasm.requeue(eventsJson);
              } catch (e) {}
            }
            AppMachinaState.isFlushing = false;
          });
      }

      doAttempt();
    } catch (e) {
      AppMachinaState.isFlushing = false;
    }
  },

  // ── Lifecycle Listeners ───────────────────────────────────────────────

  $AppMachinaSetupListeners: function () {
    if (typeof window === 'undefined') return;

    // Online/offline detection — flush on reconnect
    AppMachinaState.onlineListener = function () {
      if (!AppMachinaState.isShutDown && AppMachinaState.wasm) {
        AppMachinaFlushViaFetch();
      }
    };
    AppMachinaState.offlineListener = function () {
      // No-op, just track state
    };
    window.addEventListener('online', AppMachinaState.onlineListener);
    window.addEventListener('offline', AppMachinaState.offlineListener);

    // visibilitychange — flush on page hide using sendBeacon
    if (typeof document !== 'undefined') {
      AppMachinaState.visibilityListener = function () {
        if (
          document.visibilityState === 'hidden' &&
          AppMachinaState.wasm &&
          !AppMachinaState.isShutDown
        ) {
          // Drain ALL events via sendBeacon for reliability on page hide.
          // On mobile browsers beforeunload is unreliable, so visibilitychange
          // with 'hidden' is the primary last-chance flush point.
          try {
            var batchJson = AppMachinaState.wasm.drain(10000);
            if (batchJson !== null && batchJson !== undefined) {
              var url = AppMachinaState.wasm.eventsUrl();
              var blob = new Blob([batchJson], { type: 'application/json' });
              var sent = navigator.sendBeacon(url, blob);
              if (!sent) {
                // sendBeacon failed — requeue
                try {
                  var parsed = JSON.parse(batchJson);
                  AppMachinaState.wasm.requeue(JSON.stringify(parsed.events));
                } catch (e) {}
              }
            }
          } catch (e) {
            // Best effort
          }
        }
      };
      document.addEventListener('visibilitychange', AppMachinaState.visibilityListener);
    }

    // beforeunload — last-chance flush
    AppMachinaState.beforeUnloadListener = function () {
      if (AppMachinaState.wasm && !AppMachinaState.isShutDown) {
        try {
          var batchJson = AppMachinaState.wasm.drain(10000); // drain all
          if (batchJson !== null && batchJson !== undefined) {
            var url = AppMachinaState.wasm.eventsUrl();
            var blob = new Blob([batchJson], { type: 'application/json' });
            navigator.sendBeacon(url, blob);
          }
        } catch (e) {}
      }
    };
    window.addEventListener('beforeunload', AppMachinaState.beforeUnloadListener);
  },

  $AppMachinaCleanupListeners: function () {
    if (typeof window !== 'undefined') {
      if (AppMachinaState.onlineListener) {
        window.removeEventListener('online', AppMachinaState.onlineListener);
        AppMachinaState.onlineListener = null;
      }
      if (AppMachinaState.offlineListener) {
        window.removeEventListener('offline', AppMachinaState.offlineListener);
        AppMachinaState.offlineListener = null;
      }
      if (AppMachinaState.beforeUnloadListener) {
        window.removeEventListener('beforeunload', AppMachinaState.beforeUnloadListener);
        AppMachinaState.beforeUnloadListener = null;
      }
    }
    if (typeof document !== 'undefined' && AppMachinaState.visibilityListener) {
      document.removeEventListener('visibilitychange', AppMachinaState.visibilityListener);
      AppMachinaState.visibilityListener = null;
    }

    // Cleanup SPA deep link listeners
    AppMachinaCleanupSpaListeners();
  },

  // ── Remote Config Polling ─────────────────────────────────────────────

  // Fetch remote config from /config via fetch() and feed to the WASM core.
  // Supports ETag / 304 Not Modified to avoid unnecessary re-downloads.
  $AppMachinaConfigEtag: '',
  $AppMachinaConfigTimer: null,

  $AppMachinaFetchConfig: function () {
    if (!AppMachinaState.wasm || AppMachinaState.isShutDown) return;

    var url =
      AppMachinaState.baseUrl +
      '/config?app_id=' +
      encodeURIComponent(AppMachinaState.config.app_id) +
      '&platform=unity';
    var headers = {
      'X-App-Id': AppMachinaState.config.app_id,
      Accept: 'application/json'
    };
    if (AppMachinaConfigEtag) {
      headers['If-None-Match'] = AppMachinaConfigEtag;
    }

    fetch(url, { method: 'GET', headers: headers })
      .then(function (response) {
        if (response.status === 200) {
          var newEtag = response.headers.get('ETag') || '';
          AppMachinaConfigEtag = newEtag;
          return response.text().then(function (body) {
            if (body && AppMachinaState.wasm) {
              try {
                AppMachinaState.wasm.updateRemoteConfig(body, newEtag);
                if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
                  console.log('[AppMachina] Remote config updated');
                }
              } catch (e) {
                if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
                  console.warn('[AppMachina] Remote config update failed:', e);
                }
              }
            }
          });
        } else if (response.status === 304) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.log('[AppMachina] Remote config not modified');
          }
        }
      })
      .catch(function (e) {
        if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
          console.warn('[AppMachina] Remote config fetch failed:', e);
        }
      });
  },

  // ── Pre-Init Queue Replay ─────────────────────────────────────────────

  $AppMachinaReplayPreInitQueue: function () {
    var queue = AppMachinaState.preInitQueue;
    AppMachinaState.preInitQueue = [];
    if (queue.length === 0) return;

    if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
      console.log('[AppMachina] Replaying ' + queue.length + ' pre-init queued calls');
    }

    for (var i = 0; i < queue.length; i++) {
      var entry = queue[i];
      try {
        if (entry.method === 'track') {
          AppMachinaState.wasm.track(entry.args[0], entry.args[1], null, null);
        } else if (entry.method === 'screen') {
          AppMachinaState.wasm.screen(entry.args[0], entry.args[1], null, null);
        } else if (entry.method === 'identify') {
          AppMachinaState.wasm.identify(entry.args[0]);
        } else if (entry.method === 'group') {
          AppMachinaState.wasm.group(entry.args[0], entry.args[1]);
        } else if (entry.method === 'setUserProperties') {
          AppMachinaState.wasm.setUserProperties(entry.args[0]);
        } else if (entry.method === 'setUserPropertiesOnce') {
          AppMachinaState.wasm.setUserPropertiesOnce(entry.args[0]);
        } else if (entry.method === 'setConsent') {
          AppMachinaState.wasm.setConsent(entry.args[0]);
        } else if (entry.method === 'setDeviceContext') {
          AppMachinaState.wasm.setDeviceContext(entry.args[0]);
        }
      } catch (e) {
        if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
          console.warn('[AppMachina] Pre-init replay failed for ' + entry.method + ':', e);
        }
      }
    }
  },

  // ── Cookie Reading ────────────────────────────────────────────────────

  $AppMachinaGetCookie: function (name) {
    if (typeof document === 'undefined' || !document.cookie) return null;
    var prefix = name + '=';
    var cookies = document.cookie.split('; ');
    for (var i = 0; i < cookies.length; i++) {
      if (cookies[i].indexOf(prefix) === 0) {
        return decodeURIComponent(cookies[i].substring(prefix.length));
      }
    }
    return null;
  },

  // ── SPA Deep Link Tracking ──────────────────────────────────────

  // Attribution parameter names to look for in URLs
  $AppMachinaAttributionParams: [
    'fbclid',
    'gclid',
    'gbraid',
    'wbraid',
    'ttclid',
    'msclkid',
    'rclid',
    'twclid',
    'li_fat_id',
    'sclid',
    'irclickid',
    'utm_source',
    'utm_medium',
    'utm_campaign',
    'utm_content',
    'utm_term'
  ],

  // Build deep_link_opened properties from a URL string.
  // Returns null if the URL has no attribution params.
  $AppMachinaBuildDeepLinkProps: function (url) {
    try {
      var parsed = new URL(url);
      var params = parsed.searchParams;
      var hasAttribution = false;
      var props = {
        url: url,
        scheme: parsed.protocol.replace(':', ''),
        host: parsed.hostname,
        path: parsed.pathname
      };
      for (var i = 0; i < AppMachinaAttributionParams.length; i++) {
        var name = AppMachinaAttributionParams[i];
        var val = params.get(name);
        if (val) {
          props[name] = val;
          hasAttribution = true;
        }
      }
      if (!hasAttribution) return null;
      return props;
    } catch (e) {
      return null;
    }
  },

  // Track a deep_link_opened event via the WASM core, with 2-second deduplication.
  $AppMachinaTrackDeepLink: function (url) {
    if (!AppMachinaState.wasm || AppMachinaState.isShutDown) return;

    // Deduplicate: same URL within DEEP_LINK_DEDUP_MS window
    var now = Date.now();
    if (
      url === AppMachinaState.lastDeepLinkUrl &&
      now - AppMachinaState.lastDeepLinkTimestamp < AppMachinaState.DEEP_LINK_DEDUP_MS
    ) {
      return;
    }

    var props = AppMachinaBuildDeepLinkProps(url);
    if (!props) return;

    AppMachinaState.lastDeepLinkUrl = url;
    AppMachinaState.lastDeepLinkTimestamp = now;

    try {
      AppMachinaState.wasm.track('deep_link_opened', props, null, null);

      // Auto-flush if threshold reached
      var config = AppMachinaState.config;
      var threshold = (config && config.flush_threshold) || 20;
      if (AppMachinaState.wasm.queueDepth() >= threshold) {
        AppMachinaFlushViaFetch();
      }
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] deep_link_opened track failed:', e);
      }
    }
  },

  // Setup popstate and hashchange listeners for SPA navigation tracking
  $AppMachinaSetupSpaListeners: function () {
    if (typeof window === 'undefined') return;

    AppMachinaState.popstateListener = function () {
      AppMachinaTrackDeepLink(window.location.href);
    };
    window.addEventListener('popstate', AppMachinaState.popstateListener);

    AppMachinaState.hashchangeListener = function () {
      AppMachinaTrackDeepLink(window.location.href);
    };
    window.addEventListener('hashchange', AppMachinaState.hashchangeListener);
  },

  $AppMachinaCleanupSpaListeners: function () {
    if (typeof window === 'undefined') return;
    if (AppMachinaState.popstateListener) {
      window.removeEventListener('popstate', AppMachinaState.popstateListener);
      AppMachinaState.popstateListener = null;
    }
    if (AppMachinaState.hashchangeListener) {
      window.removeEventListener('hashchange', AppMachinaState.hashchangeListener);
      AppMachinaState.hashchangeListener = null;
    }
  },

  // ══════════════════════════════════════════════════════════════════════
  // EXPORTED FUNCTIONS (called from C# via [DllImport("__Internal")])
  // ══════════════════════════════════════════════════════════════════════

  // ── Initialization ────────────────────────────────────────────────────

  AppMachinaWebGL_Init: function (configJsonPtr) {
    var configJson = UTF8ToString(configJsonPtr);
    var config;
    try {
      config = JSON.parse(configJson);
    } catch (e) {
      console.error('[AppMachina] Failed to parse config JSON:', e);
      return;
    }

    AppMachinaState.config = config;
    AppMachinaState.isShutDown = false;
    AppMachinaState.baseUrl = (config.base_url || 'https://in.appmachina.com').replace(/\/$/, '');
    AppMachinaState.maxBatchSize = config.max_batch_size || 20;

    // Initialize the WASM core.
    // The WASM binary must be loaded asynchronously, so we start the load
    // and the wasm instance becomes available when ready.
    AppMachinaLoadWasm()
      .then(function () {
        // If shutdown was called while WASM was loading, bail out.
        // Otherwise we'd leak timers and listeners that can never be cleaned up.
        if (AppMachinaState.isShutDown) {
          return;
        }

        // Try to initialize via the global appmachina_core WASM bindings
        if (typeof Module !== 'undefined' && Module.AppMachinaWasm) {
          try {
            AppMachinaState.wasm = Module.AppMachinaWasm.init({
              appId: config.app_id,
              environment: config.environment || 'production',
              baseUrl: config.base_url,
              flushThreshold: config.flush_threshold,
              maxQueueSize: config.max_queue_size,
              maxBatchSize: config.max_batch_size,
              enableDebug: config.enable_debug,
              sdkVersion: config.sdk_version
            });
            AppMachinaState.wasmReady = true;

            // Replay any events queued before WASM was ready
            AppMachinaReplayPreInitQueue();

            // Fetch remote config now that WASM is ready (the initial call
            // outside the .then() fires too early — WASM hasn't loaded yet).
            AppMachinaFetchConfig();
          } catch (e) {
            console.warn('[AppMachina] WASM core initialization failed:', e);
          }
        }

        // Only start timers and listeners if WASM init succeeded
        if (!AppMachinaState.wasmReady) {
          console.error('[AppMachina] WASM core not available, skipping timer/listener setup');
          return;
        }

        // Fire deep_link_opened for initial URL if it contains attribution params.
        // This matches iOS/Android DeepLinksModule behavior on cold start.
        if (typeof window !== 'undefined') {
          AppMachinaTrackDeepLink(window.location.href);
        }

        // Setup SPA navigation listeners for popstate/hashchange
        AppMachinaSetupSpaListeners();

        // Start periodic flush timer only after WASM is ready (avoids
        // wasteful no-op ticks before the core can accept events).
        var intervalMs =
          (AppMachinaState.config && AppMachinaState.config.flush_interval_ms) || 30000;
        if (AppMachinaState.flushTimer) clearInterval(AppMachinaState.flushTimer);
        AppMachinaState.flushTimer = setInterval(function () {
          if (
            !AppMachinaState.isShutDown &&
            AppMachinaState.wasm &&
            AppMachinaState.wasm.queueDepth() > 0
          ) {
            AppMachinaFlushViaFetch();
          }
        }, intervalMs);

        // Start remote config polling (5 minute interval) after WASM is ready.
        if (typeof AppMachinaConfigTimer !== 'undefined' && AppMachinaConfigTimer)
          clearInterval(AppMachinaConfigTimer);
        AppMachinaConfigTimer = setInterval(function () {
          AppMachinaFetchConfig();
        }, 300000);
      })
      .catch(function (e) {
        console.warn('[AppMachina] WASM binary load failed, using JS fallback:', e);
      });

    // Setup lifecycle listeners (these don't require WASM — they handle
    // visibility/online events and are needed from the start)
    AppMachinaSetupListeners();
  },

  // ── Event Tracking ────────────────────────────────────────────────────

  AppMachinaWebGL_Track: function (eventNamePtr, propertiesJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var eventName = UTF8ToString(eventNamePtr);
    var propsJson = propertiesJsonPtr ? UTF8ToString(propertiesJsonPtr) : null;

    // Queue events that arrive before WASM is ready
    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var props = null;
        try {
          props = propsJson ? JSON.parse(propsJson) : null;
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init track: failed to parse properties JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'track', args: [eventName, props] });
      } else if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] Pre-init queue full, dropping event: ' + eventName);
      }
      return;
    }

    try {
      var props = propsJson ? JSON.parse(propsJson) : null;
      AppMachinaState.wasm.track(eventName, props, null, null);

      // Auto-flush if threshold reached
      var config = AppMachinaState.config;
      var threshold = (config && config.flush_threshold) || 20;
      if (AppMachinaState.wasm.queueDepth() >= threshold) {
        AppMachinaFlushViaFetch();
      }
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL track failed:', e);
      }
    }
  },

  AppMachinaWebGL_Screen: function (screenNamePtr, propertiesJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var screenName = UTF8ToString(screenNamePtr);
    var propsJson = propertiesJsonPtr ? UTF8ToString(propertiesJsonPtr) : null;

    // Queue screen calls that arrive before WASM is ready
    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var props = null;
        try {
          props = propsJson ? JSON.parse(propsJson) : null;
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init screen: failed to parse properties JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'screen', args: [screenName, props] });
      } else if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] Pre-init queue full, dropping screen: ' + screenName);
      }
      return;
    }

    try {
      var props = propsJson ? JSON.parse(propsJson) : null;
      AppMachinaState.wasm.screen(screenName, props, null, null);

      var config = AppMachinaState.config;
      var threshold = (config && config.flush_threshold) || 20;
      if (AppMachinaState.wasm.queueDepth() >= threshold) {
        AppMachinaFlushViaFetch();
      }
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL screen failed:', e);
      }
    }
  },

  // ── User Identity ─────────────────────────────────────────────────────

  AppMachinaWebGL_Identify: function (userIdPtr) {
    if (AppMachinaState.isShutDown) return;

    var userId = UTF8ToString(userIdPtr);

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        AppMachinaState.preInitQueue.push({ method: 'identify', args: [userId] });
      }
      return;
    }

    try {
      AppMachinaState.wasm.identify(userId);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL identify failed:', e);
      }
    }
  },

  AppMachinaWebGL_Group: function (groupIdPtr, propertiesJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var groupId = UTF8ToString(groupIdPtr);
    var propsJson = propertiesJsonPtr ? UTF8ToString(propertiesJsonPtr) : null;

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var props = null;
        try {
          props = propsJson ? JSON.parse(propsJson) : null;
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init group: failed to parse properties JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'group', args: [groupId, props] });
      }
      return;
    }

    try {
      var props = propsJson ? JSON.parse(propsJson) : null;
      AppMachinaState.wasm.group(groupId, props);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL group failed:', e);
      }
    }
  },

  AppMachinaWebGL_SetUserProperties: function (propertiesJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var propsJson = UTF8ToString(propertiesJsonPtr);

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var props = {};
        try {
          props = JSON.parse(propsJson);
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init setUserProperties: failed to parse JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'setUserProperties', args: [props] });
      }
      return;
    }

    try {
      var props = JSON.parse(propsJson);
      AppMachinaState.wasm.setUserProperties(props);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL setUserProperties failed:', e);
      }
    }
  },

  AppMachinaWebGL_SetUserPropertiesOnce: function (propertiesJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var propsJson = UTF8ToString(propertiesJsonPtr);

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var props = {};
        try {
          props = JSON.parse(propsJson);
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init setUserPropertiesOnce: failed to parse JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'setUserPropertiesOnce', args: [props] });
      }
      return;
    }

    try {
      var props = JSON.parse(propsJson);
      AppMachinaState.wasm.setUserPropertiesOnce(props);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL setUserPropertiesOnce failed:', e);
      }
    }
  },

  // ── Consent ───────────────────────────────────────────────────────────

  AppMachinaWebGL_SetConsent: function (consentJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var consentJson = UTF8ToString(consentJsonPtr);

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var consent = {};
        try {
          consent = JSON.parse(consentJson);
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init setConsent: failed to parse JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'setConsent', args: [consent] });
      }
      return;
    }

    try {
      var consent = JSON.parse(consentJson);
      AppMachinaState.wasm.setConsent(consent);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL setConsent failed:', e);
      }
    }
  },

  // ── Device Context ────────────────────────────────────────────────────

  AppMachinaWebGL_SetDeviceContext: function (contextJsonPtr) {
    if (AppMachinaState.isShutDown) return;

    var contextJson = UTF8ToString(contextJsonPtr);

    if (!AppMachinaState.wasm) {
      if (AppMachinaState.preInitQueue.length < AppMachinaState.PRE_INIT_MAX) {
        var context = {};
        try {
          context = JSON.parse(contextJson);
        } catch (e) {
          if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
            console.warn('[AppMachina] Pre-init setDeviceContext: failed to parse JSON:', e);
          }
        }
        AppMachinaState.preInitQueue.push({ method: 'setDeviceContext', args: [context] });
      }
      return;
    }

    try {
      var context = JSON.parse(contextJson);
      AppMachinaState.wasm.setDeviceContext(context);
    } catch (e) {
      if (AppMachinaState.config && AppMachinaState.config.enable_debug) {
        console.warn('[AppMachina] WebGL setDeviceContext failed:', e);
      }
    }
  },

  // ── Flush / Drain ─────────────────────────────────────────────────────

  AppMachinaWebGL_Flush: function () {
    if (AppMachinaState.isShutDown) return;
    AppMachinaFlushViaFetch();
  },

  AppMachinaWebGL_FlushBlocking: function () {
    // In WebGL, we can't do a truly blocking flush.
    // Use sendBeacon for best-effort delivery during shutdown.
    if (!AppMachinaState.wasm || AppMachinaState.isShutDown) return;

    try {
      var batchJson = AppMachinaState.wasm.drain(10000);
      if (batchJson !== null && batchJson !== undefined) {
        var url = AppMachinaState.wasm.eventsUrl();
        var blob = new Blob([batchJson], { type: 'application/json' });
        navigator.sendBeacon(url, blob);
      }
    } catch (e) {
      // Best effort
    }
  },

  AppMachinaWebGL_DrainBatch: function (count) {
    if (!AppMachinaState.wasm) return 0;

    try {
      var batchJson = AppMachinaState.wasm.drain(count);
      if (batchJson === null || batchJson === undefined) return 0;
      return AppMachinaAllocString(batchJson);
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_RequeueEvents: function (eventsJsonPtr) {
    if (!AppMachinaState.wasm) return;

    var eventsJson = UTF8ToString(eventsJsonPtr);
    try {
      AppMachinaState.wasm.requeue(eventsJson);
    } catch (e) {
      // Requeue failed — events lost
    }
  },

  AppMachinaWebGL_FlushHeaders: function () {
    if (!AppMachinaState.wasm) return 0;

    try {
      var headers = AppMachinaState.wasm.flushHeaders();
      return AppMachinaAllocString(JSON.stringify(headers));
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_EventsUrl: function () {
    if (!AppMachinaState.wasm) return 0;

    try {
      var url = AppMachinaState.wasm.eventsUrl();
      return AppMachinaAllocString(url);
    } catch (e) {
      return 0;
    }
  },

  // ── Queue State ───────────────────────────────────────────────────────

  AppMachinaWebGL_QueueDepth: function () {
    if (!AppMachinaState.wasm) return -1;
    try {
      return AppMachinaState.wasm.queueDepth();
    } catch (e) {
      return -1;
    }
  },

  AppMachinaWebGL_IsInitialized: function () {
    return AppMachinaState.wasm !== null && !AppMachinaState.isShutDown ? 1 : 0;
  },

  // ── Session ───────────────────────────────────────────────────────────

  AppMachinaWebGL_GetSessionId: function () {
    if (!AppMachinaState.wasm) return 0;
    try {
      var sessionId = AppMachinaState.wasm.getSessionId();
      return AppMachinaAllocString(sessionId);
    } catch (e) {
      return 0;
    }
  },

  // ── Remote Config ─────────────────────────────────────────────────────

  AppMachinaWebGL_GetRemoteConfigJson: function () {
    if (!AppMachinaState.wasm) return 0;
    try {
      var json = AppMachinaState.wasm.getRemoteConfigJson();
      if (json === null || json === undefined) return 0;
      return AppMachinaAllocString(json);
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_UpdateRemoteConfig: function (configJsonPtr, etagPtr) {
    if (!AppMachinaState.wasm) return;

    var configJson = UTF8ToString(configJsonPtr);
    var etag = etagPtr ? UTF8ToString(etagPtr) : null;

    try {
      AppMachinaState.wasm.updateRemoteConfig(configJson, etag);
    } catch (e) {
      // Best effort
    }
  },

  // ── Shutdown ──────────────────────────────────────────────────────────

  AppMachinaWebGL_Shutdown: function () {
    AppMachinaState.isShutDown = true;

    // Stop periodic flush
    if (AppMachinaState.flushTimer) {
      clearInterval(AppMachinaState.flushTimer);
      AppMachinaState.flushTimer = null;
    }

    // Stop remote config polling
    if (AppMachinaConfigTimer) {
      clearInterval(AppMachinaConfigTimer);
      AppMachinaConfigTimer = null;
    }

    // Cleanup listeners
    AppMachinaCleanupListeners();

    // Last-chance flush via sendBeacon
    if (AppMachinaState.wasm) {
      try {
        var batchJson = AppMachinaState.wasm.drain(10000);
        if (batchJson !== null && batchJson !== undefined) {
          var url = AppMachinaState.wasm.eventsUrl();
          var blob = new Blob([batchJson], { type: 'application/json' });
          navigator.sendBeacon(url, blob);
        }
      } catch (e) {}

      try {
        AppMachinaState.wasm.shutdown();
      } catch (e) {}
    }

    AppMachinaState.wasm = null;
    AppMachinaState.wasmReady = false;
  },

  // ── CAPI Properties ───────────────────────────────────────────────────
  // Meta _fbp, TikTok _ttp, page URL, fbc — same as @appmachina/client/capi.ts

  AppMachinaWebGL_GetFbpCookie: function () {
    var value = AppMachinaGetCookie('_fbp');
    return AppMachinaAllocString(value);
  },

  AppMachinaWebGL_GetTtpCookie: function () {
    var value = AppMachinaGetCookie('_ttp');
    return AppMachinaAllocString(value);
  },

  AppMachinaWebGL_GetPageUrl: function () {
    if (typeof window === 'undefined') return 0;
    try {
      return AppMachinaAllocString(window.location.href);
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_GetFbc: function () {
    // Check for fbclid in URL params first
    if (typeof window !== 'undefined') {
      try {
        var params = new URLSearchParams(window.location.search);
        var fbclid = params.get('fbclid');
        if (fbclid) {
          var fbc = 'fb.1.' + Date.now() + '.' + fbclid;
          return AppMachinaAllocString(fbc);
        }
      } catch (e) {}
    }
    // Fall back to _fbc cookie
    var value = AppMachinaGetCookie('_fbc');
    return AppMachinaAllocString(value);
  },

  // ── Attribution URL Parameters ────────────────────────────────────────

  AppMachinaWebGL_GetUrlParameters: function () {
    if (typeof window === 'undefined') return 0;
    try {
      var params = new URLSearchParams(window.location.search);
      var result = {};
      var clickIdParams = [
        'fbclid',
        'gclid',
        'gbraid',
        'wbraid',
        'ttclid',
        'msclkid',
        'rclid',
        'twclid',
        'li_fat_id',
        'sclid',
        'irclickid'
      ];
      var utmParams = ['utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term'];
      var allParams = clickIdParams.concat(utmParams);

      for (var i = 0; i < allParams.length; i++) {
        var val = params.get(allParams[i]);
        if (val) result[allParams[i]] = val;
      }

      if (typeof document !== 'undefined' && document.referrer) {
        result['referrer_url'] = document.referrer;
      }

      if (Object.keys(result).length === 0) return 0;
      return AppMachinaAllocString(JSON.stringify(result));
    } catch (e) {
      return 0;
    }
  },

  // ── Online/Offline ────────────────────────────────────────────────────

  AppMachinaWebGL_IsOnline: function () {
    return navigator.onLine ? 1 : 0;
  },

  // ── localStorage Persistence ──────────────────────────────────────────

  AppMachinaWebGL_SetItem: function (keyPtr, valuePtr) {
    try {
      var key = UTF8ToString(keyPtr);
      var value = UTF8ToString(valuePtr);
      localStorage.setItem(key, value);
    } catch (e) {
      // localStorage unavailable or full
    }
  },

  AppMachinaWebGL_GetItem: function (keyPtr) {
    try {
      var key = UTF8ToString(keyPtr);
      var value = localStorage.getItem(key);
      return AppMachinaAllocString(value);
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_RemoveItem: function (keyPtr) {
    try {
      var key = UTF8ToString(keyPtr);
      localStorage.removeItem(key);
    } catch (e) {
      // Best effort
    }
  },

  // ── Browser Info ──────────────────────────────────────────────────────

  AppMachinaWebGL_GetUserAgent: function () {
    if (typeof navigator === 'undefined') return 0;
    return AppMachinaAllocString(navigator.userAgent);
  },

  AppMachinaWebGL_GetLanguage: function () {
    if (typeof navigator === 'undefined') return 0;
    return AppMachinaAllocString(navigator.language || 'en-US');
  },

  AppMachinaWebGL_GetScreenSize: function () {
    if (typeof screen === 'undefined') return 0;
    return AppMachinaAllocString(screen.width + 'x' + screen.height);
  },

  AppMachinaWebGL_GetTimezone: function () {
    try {
      return AppMachinaAllocString(Intl.DateTimeFormat().resolvedOptions().timeZone);
    } catch (e) {
      return 0;
    }
  },

  AppMachinaWebGL_GetPlatformOS: function () {
    if (typeof navigator === 'undefined') return 0;
    var ua = navigator.userAgent;
    if (ua.indexOf('Windows') >= 0) return AppMachinaAllocString('Windows');
    if (ua.indexOf('Mac OS') >= 0) return AppMachinaAllocString('macOS');
    if (ua.indexOf('Android') >= 0) return AppMachinaAllocString('Android');
    if (ua.indexOf('iPhone') >= 0 || ua.indexOf('iPad') >= 0) return AppMachinaAllocString('iOS');
    if (ua.indexOf('Linux') >= 0) return AppMachinaAllocString('Linux');
    return AppMachinaAllocString('WebGL');
  }
};

// Wire up dependencies between $ functions and exported functions
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaState');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaLoadWasm');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaAllocString');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaCheckCircuit');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaRecordSuccess');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaRecordFailure');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaUpdateRetryAfter');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaIsRetryAfterActive');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaFlushViaFetch');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaSetupListeners');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaCleanupListeners');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaReplayPreInitQueue');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaConfigEtag');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaConfigTimer');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaFetchConfig');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaGetCookie');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaAttributionParams');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaBuildDeepLinkProps');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaTrackDeepLink');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaSetupSpaListeners');
autoAddDeps(AppMachinaWebGLPlugin, '$AppMachinaCleanupSpaListeners');

mergeInto(LibraryManager.library, AppMachinaWebGLPlugin);
