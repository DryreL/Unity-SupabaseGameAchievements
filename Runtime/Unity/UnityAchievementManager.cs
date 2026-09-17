using System;
using System.IO;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Unity host for the achievement system: loads the packaged manifest, wires storage, sync, launcher
    /// settings and the overlay, then pumps <see cref="AchievementManager.Tick"/> every frame.
    /// Gameplay code only ever talks to the static <see cref="AchievementManager"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// // Once, at boot (e.g. in your bootstrap scene):
    /// UnityAchievementManager.Create(config, authProvider, new UnityLocalizationProvider());
    ///
    /// // Anywhere in gameplay:
    /// AchievementManager.TryUnlock("first_blood");
    /// </code>
    /// </example>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class UnityAchievementManager : MonoBehaviour
    {
        [Tooltip("Achievement manifest exported from the backend (scripts/export-achievement-catalog.mjs). Always required as the offline fallback, even when Remote Config below is also set.")]
        [SerializeField] private TextAsset _catalogJson;

        [Header("Remote Config (optional)")]
        [Tooltip("Remote Config key holding an updated manifest JSON (same shape as the bundled file). " +
            "Leave empty to use only the bundled manifest above. Read only from whatever Remote Config already " +
            "has fetched — this never starts a fetch itself; something else in the project must already do that.")]
        [SerializeField] private string _remoteConfigKey = "";

        [Header("Backend (leave empty for local-only achievements)")]
        [SerializeField] private string _supabaseUrl = "";
        [Tooltip("Public publishable/anon key. Never a service role key.")]
        [SerializeField] private string _supabasePublishableKey = "";

        [Header("Notifications")]
        [SerializeField] private bool _enableOverlay = true;
        [SerializeField] private UnityAchievementOverlay _overlay;
        [Tooltip("Folder (under the OS per-user config directory) containing the launcher's shared settings file. Empty = always enabled.")]
        [SerializeField] private string _sharedSettingsFolder = "";
        [SerializeField] private string _sharedSettingsFileName = "shared-settings.json";
        [Tooltip("Optional prefix for Resources.Load of achievement icons.")]
        [SerializeField] private string _iconResourcesPrefix = "";
        [SerializeField] private Sprite _fallbackIcon;

        [Header("Diagnostics")]
        [SerializeField] private bool _verboseLogging;

        private AchievementSystem _system;
        private IAchievementNotificationSettingsProvider _settings;
        private UnityAchievementLogger _logger;
        private NetworkReachability _reachability;
        private RemoteConfigAchievementCatalogSource _remoteConfigSource;
        private IAchievementAuthProvider _lastAuth;
        private IAchievementLocalizationProvider _lastLocalization;

        public static UnityAchievementManager Instance { get; private set; }

        public AchievementSystem Runtime => _system;

        public UnityAchievementOverlay Overlay => _overlay;

        /// <summary>Shared transport, for auth providers that need to call the backend too.</summary>
        public static readonly UnityWebRequestTransport Transport = new UnityWebRequestTransport();

        [Serializable]
        public sealed class Config
        {
            public TextAsset CatalogJson;
            public string RemoteConfigKey = "";
            public string SupabaseUrl = "";
            public string SupabasePublishableKey = "";
            public bool EnableOverlay = true;
            public string SharedSettingsFolder = "";
            public string SharedSettingsFileName = "shared-settings.json";
            public string IconResourcesPrefix = "";
            public Sprite FallbackIcon;
            public AudioClip UnlockSound;
            public bool VerboseLogging;
        }

        /// <summary>Creates a persistent host object and initializes the achievement system.</summary>
        public static UnityAchievementManager Create(Config config, IAchievementAuthProvider auth = null, IAchievementLocalizationProvider localization = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (Instance != null)
            {
                Debug.LogWarning("[Achievements] UnityAchievementManager already exists; reusing it.");
                return Instance;
            }

            var go = new GameObject("AchievementManager");
            go.SetActive(false); // configure before Awake
            var host = go.AddComponent<UnityAchievementManager>();
            host._catalogJson = config.CatalogJson;
            host._remoteConfigKey = config.RemoteConfigKey;
            host._supabaseUrl = config.SupabaseUrl;
            host._supabasePublishableKey = config.SupabasePublishableKey;
            host._enableOverlay = config.EnableOverlay;
            host._sharedSettingsFolder = config.SharedSettingsFolder;
            host._sharedSettingsFileName = config.SharedSettingsFileName;
            host._iconResourcesPrefix = config.IconResourcesPrefix;
            host._fallbackIcon = config.FallbackIcon;
            host._verboseLogging = config.VerboseLogging;
            go.SetActive(true);

            host.Initialize(auth, localization);
            if (config.UnlockSound != null && host._overlay != null) host._overlay.UnlockSound = config.UnlockSound;
            return host;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _logger = new UnityAchievementLogger(_verboseLogging);
            AchievementManager.Logger = _logger;
            _reachability = Application.internetReachability;
        }

        /// <summary>
        /// Initializes (or re-initializes) the system. Safe to call with no auth provider: achievements then
        /// work fully offline and sync once a provider is supplied by a later call.
        /// Never throws for configuration problems — the game keeps running without achievements.
        /// </summary>
        public void Initialize(IAchievementAuthProvider auth = null, IAchievementLocalizationProvider localization = null)
        {
            if (_logger == null)
            {
                _logger = new UnityAchievementLogger(_verboseLogging);
                AchievementManager.Logger = _logger;
            }

            AchievementCatalog catalog;
            try
            {
                if (_catalogJson == null) throw new AchievementCatalogException("No achievement manifest assigned.");
                catalog = AchievementCatalog.FromJson(_catalogJson.text);
            }
            catch (Exception e)
            {
                _logger.Error("Achievements disabled: " + e.Message);
                return;
            }

            if (!string.IsNullOrEmpty(_remoteConfigKey))
            {
                try
                {
                    _remoteConfigSource = new RemoteConfigAchievementCatalogSource(
                        _remoteConfigKey,
                        Path.Combine(Application.persistentDataPath, "achievements", catalog.GameSlug, "remote-catalog.json"),
                        _logger);
                    catalog = _remoteConfigSource.ResolveBest(catalog);
                }
                catch (Exception e)
                {
                    _logger.Warning("Remote Config achievement catalog check failed; using the bundled manifest. " + e.Message);
                }
            }

            InitializeWithCatalog(catalog, auth, localization);
        }

        /// <summary>
        /// Re-checks Remote Config for a newer achievement catalog and hot-swaps it in if found. Call this
        /// once your project's own Remote Config fetch completes (see
        /// <see cref="RemoteConfigAchievementCatalogSource"/>). Local unlock/pending state carries over
        /// unaffected. No-op if Remote Config was not configured or the system has not started yet. Never
        /// blocks and never throws.
        /// </summary>
        public void RefreshCatalogFromRemoteConfig()
        {
            if (_remoteConfigSource == null || _system == null) return;
            try
            {
                if (_remoteConfigSource.TryRefresh(_system.Catalog, out var refreshed))
                {
                    _logger.Info("Applying updated achievement catalog from Remote Config (v" + refreshed.CatalogVersion + ").");
                    InitializeWithCatalog(refreshed, _lastAuth, _lastLocalization);
                }
            }
            catch (Exception e)
            {
                _logger.Error("Failed to apply Remote Config achievement catalog: " + e);
            }
        }

        private void InitializeWithCatalog(AchievementCatalog catalog, IAchievementAuthProvider auth, IAchievementLocalizationProvider localization)
        {
            _lastAuth = auth;
            _lastLocalization = localization;

            try
            {
                string storageDirectory = Path.Combine(Application.persistentDataPath, "achievements", catalog.GameSlug);
                bool hasBackend = !string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_supabasePublishableKey);
                if (!hasBackend && auth != null)
                    _logger.Warning("An auth provider was supplied but no Supabase URL/key is configured; achievements stay local-only.");

                _settings = !_enableOverlay
                    ? null
                    : string.IsNullOrEmpty(_sharedSettingsFolder)
                        ? (IAchievementNotificationSettingsProvider)new FixedAchievementNotificationSettings(true)
                        : new UnityAchievementSettingsProvider(_sharedSettingsFolder, _sharedSettingsFileName, _logger);

                _system = AchievementManager.Initialize(new AchievementSystemOptions
                {
                    Catalog = catalog,
                    Storage = new FileAchievementStorage(storageDirectory),
                    ApiClient = hasBackend ? new SupabaseAchievementApiClient(_supabaseUrl, _supabasePublishableKey, Transport) : null,
                    AuthProvider = auth ?? OfflineAchievementAuthProvider.Instance,
                    NotificationSettings = _settings,
                    Localization = localization ?? DefaultAchievementLocalizationProvider.Instance,
                    Logger = _logger,
                    Dispatcher = UnityMainThreadDispatcher.Instance,
                    BackgroundWrites = Application.platform != RuntimePlatform.WebGLPlayer,
                });

                if (_system.Notifications != null)
                {
                    if (_overlay == null) _overlay = gameObject.AddComponent<UnityAchievementOverlay>();
                    _overlay.Bind(_system.Notifications, new ResourcesAchievementIconProvider(_iconResourcesPrefix, _fallbackIcon));
                }

                _logger.Info("Initialized " + catalog.Count + " achievements for '" + catalog.GameSlug + "' (catalog v" + catalog.CatalogVersion + ", " + _system.UnlockedCount + " unlocked, " + _system.PendingSyncCount + " pending sync).");
            }
            catch (Exception e)
            {
                _logger.Error("Achievement system failed to start; gameplay continues without it. " + e);
            }
        }

        private void Update()
        {
            if (_system == null) return;

            var reachability = Application.internetReachability;
            if (reachability != _reachability)
            {
                if (_reachability == NetworkReachability.NotReachable) _system.NotifyNetworkAvailable();
                _reachability = reachability;
            }

            try
            {
                _system.Tick();
            }
            catch (Exception e)
            {
                _logger.Error("Achievement tick failed: " + e);
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) _settings?.Refresh();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) _system?.OnApplicationPausing();
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Shutdown();
                Instance = null;
            }
        }

        private void Shutdown()
        {
            if (_system == null) return;
            if (AchievementManager.Current == _system) AchievementManager.Shutdown();
            else _system.Dispose();
            _system = null;
        }
    }
}

