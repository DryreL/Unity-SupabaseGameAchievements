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
        [Tooltip("Toast look authored in the Achievement Dashboard's Overlay tab (overlay.json). Empty = load Resources/<Overlay Resource>; if neither exists the Overlay component's own values are used.")]
        [SerializeField] private TextAsset _overlayJson;
        [SerializeField] private string _overlayResource = AchievementOverlaySettings.DefaultResource;
        [Tooltip("Folder (under the OS per-user config directory) containing the launcher's shared settings file. Empty = always enabled.")]
        [SerializeField] private string _sharedSettingsFolder = "";
        [SerializeField] private string _sharedSettingsFileName = "shared-settings.json";
        [Tooltip("Optional prefix for Resources.Load of achievement icons.")]
        [SerializeField] private string _iconResourcesPrefix = "";
        [SerializeField] private Sprite _fallbackIcon;
        [Tooltip("Auto follows the manifest's iconStyle (Combined when it has none). Layered draws one shared background behind every achievement's own icon.")]
        [SerializeField] private AchievementIconStyleSetting _iconStyle = AchievementIconStyleSetting.Auto;
        [Tooltip("Layered style: the shared background sprite. Empty = load the manifest's iconBackground (or the path below) from Resources.")]
        [SerializeField] private Sprite _iconBackground;
        [Tooltip("Layered style: Resources path of the background, relative to the icon prefix, no extension. Empty = the manifest's iconBackground.")]
        [SerializeField] private string _iconBackgroundResource = "";

        [Header("Achievement rules")]
        [Tooltip("Rules authored in the Achievement Dashboard (rules.json). Empty = load Resources/<Rules Resource>; if neither exists there are simply no dashboard rules.")]
        [SerializeField] private TextAsset _rulesJson;
        [SerializeField] private string _rulesResource = "Achievements/rules";

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

        /// <summary>True when an overlay.json from the dashboard was found and applied to the toast.</summary>
        public bool OverlaySettingsApplied { get; private set; }

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
            public AchievementIconStyleSetting IconStyle = AchievementIconStyleSetting.Auto;
            public Sprite IconBackground;
            public string IconBackgroundResource = "";
            public TextAsset RulesJson;
            public string RulesResource = "Achievements/rules";
            public TextAsset OverlayJson;
            public string OverlayResource = AchievementOverlaySettings.DefaultResource;
            public AudioClip UnlockSound;
            public float ToastHoldDuration = 4.5f;
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
            host._iconStyle = config.IconStyle;
            host._iconBackground = config.IconBackground;
            host._iconBackgroundResource = config.IconBackgroundResource;
            host._rulesJson = config.RulesJson;
            host._rulesResource = config.RulesResource;
            host._overlayJson = config.OverlayJson;
            host._overlayResource = config.OverlayResource;
            host._verboseLogging = config.VerboseLogging;
            go.SetActive(true);

            host.Initialize(auth, localization);
            // A dashboard overlay.json is the whole look, so it wins over these two code-side defaults.
            if (host._overlay != null && !host.OverlaySettingsApplied)
            {
                if (config.UnlockSound != null) host._overlay.UnlockSound = config.UnlockSound;
                if (config.ToastHoldDuration > 0f) host._overlay.HoldDuration = config.ToastHoldDuration;
            }
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

        /// <summary>
        /// Clears all local storage files (unlocked and pending achievements) on disk for this game
        /// and re-initializes an empty local state.
        /// </summary>
        public void ResetLocalState()
        {
            if (_system != null)
            {
                string storageDirectory = Path.Combine(Application.persistentDataPath, "achievements", _system.Catalog.GameSlug);
                if (Directory.Exists(storageDirectory))
                {
                    try
                    {
                        Directory.Delete(storageDirectory, true);
                    }
                    catch (Exception e)
                    {
                        _logger?.Warning("Failed to delete local achievements directory: " + e.Message);
                    }
                }
            }
            Initialize(_lastAuth, _lastLocalization);
            _logger?.Info("Local achievements state has been reset.");
        }


        private static IAchievementLocalizationProvider AutoDetectLocalizationProvider(IAchievementLogger logger)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("DryreLHub.SupabaseGameAchievements.Unity.UnityLocalizationProvider");
                    if (type != null)
                    {
                        var provider = Activator.CreateInstance(type, new object[] { logger }) as IAchievementLocalizationProvider;
                        if (provider != null)
                        {
                            logger?.Info("Auto-detected and activated UnityLocalizationProvider for achievement localization.");
                            return provider;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warning("Failed to auto-create UnityLocalizationProvider: " + ex.Message);
            }
            return DefaultAchievementLocalizationProvider.Instance;
        }
        private void InitializeWithCatalog(AchievementCatalog catalog, IAchievementAuthProvider auth, IAchievementLocalizationProvider localization)
        {
            _lastAuth = auth;
            _lastLocalization = localization;

            try
            {
                string storageDirectory = Path.Combine(Application.persistentDataPath, "achievements", catalog.GameSlug);
#if UNITY_IOS
                UnityEngine.iOS.Device.SetNoBackupFlag(storageDirectory);
#endif
                bool hasBackend = !string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_supabasePublishableKey);
                if (!hasBackend && auth != null)
                    _logger.Warning("An auth provider was supplied but no Supabase URL/key is configured; achievements stay local-only.");

                var effectiveLocalization = localization ?? AutoDetectLocalizationProvider(_logger);

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
                    Localization = effectiveLocalization,
                    Logger = _logger,
                    Dispatcher = UnityMainThreadDispatcher.Instance,
                    BackgroundWrites = Application.platform != RuntimePlatform.WebGLPlayer,
                });

                if (_system.Notifications != null)
                {
                    if (_overlay == null) _overlay = gameObject.AddComponent<UnityAchievementOverlay>();
                    _overlay.Bind(_system.Notifications, new ResourcesAchievementIconProvider(_iconResourcesPrefix, _fallbackIcon), effectiveLocalization);
                    ApplyIconStyle(catalog);
                    ApplyOverlaySettings();
                }

                EnsureRulesRunner();

                _logger.Info("Initialized " + catalog.Count + " achievements for '" + catalog.GameSlug + "' (catalog v" + catalog.CatalogVersion + ", " + _system.UnlockedCount + " unlocked, " + _system.PendingSyncCount + " pending sync).");
            }
            catch (Exception e)
            {
                _logger.Error("Achievement system failed to start; gameplay continues without it. " + e);
            }
        }

        /// <summary>Applies the dashboard's overlay.json (colors, fonts, position, timing, sound, custom prefab) if there is one.</summary>
        private void ApplyOverlaySettings()
        {
            string json = _overlayJson != null ? _overlayJson.text : null;
            if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(_overlayResource))
            {
                var asset = Resources.Load<TextAsset>(_overlayResource);
                json = asset != null ? asset.text : null;
            }
            if (string.IsNullOrEmpty(json)) return;

            if (!AchievementOverlaySettings.TryParse(json, out var settings))
            {
                _logger.Warning("The overlay settings file could not be read; the toast keeps its Inspector values. Fix or delete Resources/" + _overlayResource + ".json.");
                return;
            }

            _overlay.ApplySettings(settings);
            OverlaySettingsApplied = true;
        }

        /// <summary>Starts the rules the dashboard authored, once, if a rules.json exists. Missing file = no rules, no error.</summary>
        private void EnsureRulesRunner()
        {
            if (GetComponent<AchievementRulesRunner>() != null) return;

            string json = _rulesJson != null ? _rulesJson.text : null;
            if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(_rulesResource))
            {
                var asset = Resources.Load<TextAsset>(_rulesResource);
                json = asset != null ? asset.text : null;
            }
            if (string.IsNullOrEmpty(json)) return;

            gameObject.AddComponent<AchievementRulesRunner>().Initialize(json, _logger);
        }

        /// <summary>Resolves the icon style (Inspector/Config override, else the manifest) and hands it to the overlay.</summary>
        private void ApplyIconStyle(AchievementCatalog catalog)
        {
            var style = _iconStyle == AchievementIconStyleSetting.Combined ? AchievementIconStyle.Combined
                : _iconStyle == AchievementIconStyleSetting.Layered ? AchievementIconStyle.Layered
                : catalog.IconStyle;

            Sprite background = null;
            if (style == AchievementIconStyle.Layered)
            {
                background = _iconBackground;
                if (background == null)
                {
                    string path = !string.IsNullOrEmpty(_iconBackgroundResource) ? _iconBackgroundResource : catalog.IconBackground;
                    if (!string.IsNullOrEmpty(path))
                    {
                        int dot = path.LastIndexOf('.');
                        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
                        background = Resources.Load<Sprite>(_iconResourcesPrefix + (dot > slash ? path.Substring(0, dot) : path));
                    }
                    if (background == null)
                        _logger.Warning("Layered icon style is on but its background '" + (path ?? "(none)") + "' was not found under Resources/" + _iconResourcesPrefix + "; using combined icons.");
                }
            }

            _overlay.ConfigureIconStyle(style, background, catalog.IconInset);
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


