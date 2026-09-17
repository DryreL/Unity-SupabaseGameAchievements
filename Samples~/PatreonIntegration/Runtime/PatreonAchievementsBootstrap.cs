using System;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Patreon
{
    /// <summary>
    /// Ready-to-use achievements bootstrap for games built on DryreL Hub's Unity Patreon Authenticator.
    /// Add it to a GameObject (or use <b>Tools &gt; DryreL Hub &gt; Setup Achievements (Patreon) In Scene</b>,
    /// which does that for you) and configure the Inspector fields — no code required.
    /// </summary>
    /// <remarks>
    /// This assembly hard-references <c>DryreLHub.UnityPatreonAuthenticator</c> on purpose: it exists
    /// specifically for DryreL Hub games that already use that plugin. A project without it will not compile
    /// this one assembly (nothing else in the package is affected) — use the portable
    /// <c>ExampleIntegration</c> sample instead if you are not using that plugin.
    /// <para>
    /// Supabase URL/publishable key auto-fill from <c>Resources/PatreonConfig.asset</c> when left blank.
    /// The launcher shared-settings folder defaults to <see cref="Application.companyName"/>, so it matches
    /// the launcher automatically for any DryreL Hub game without hardcoding a studio name here.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-900)] // after UnityAchievementManager's own -1000
    public sealed class PatreonAchievementsBootstrap : MonoBehaviour
    {
        private const string PatreonConfigResourcePath = "PatreonConfig"; // Assets/Resources/PatreonConfig.asset
        private const string MiddlewareSuffix = "/functions/v1/patreon-middleware";
        private const string UnityLocalizationProviderTypeName =
            "DryreLHub.SupabaseGameAchievements.Unity.UnityLocalizationProvider, DryreLHub.SupabaseGameAchievements.Unity.Localization";

        [Tooltip("Achievement manifest exported with Tools > DryreL Hub > Export Achievement Catalog (or " +
            "scripts/export-achievement-catalog.mjs). Always required as the offline fallback, even when " +
            "Remote Config below is also used. Leave empty to load it from the Manifest Resource Path below.")]
        [SerializeField] private TextAsset _manifest;

        [Tooltip("Used only when Manifest above is left empty: a Resources.Load path (no extension), " +
            "e.g. \"Achievements/achievements\" for Assets/Resources/Achievements/achievements.json.")]
        [SerializeField] private string _manifestResourcePath = "Achievements/achievements";

        [Tooltip("Unity Remote Config key holding an updated manifest JSON for this game. Must match a key " +
            "you create in the Remote Config dashboard, value type Json or String, value = the exported " +
            "manifest JSON. Leave empty to use only the bundled manifest above.")]
        [SerializeField] private string _remoteConfigKey = "achievement_catalog";

        [Tooltip("How often (seconds) to re-check Remote Config for a newer catalog. This is a cheap, " +
            "synchronous, in-memory check (see RemoteConfigAchievementCatalogSource) - it never starts a " +
            "network fetch itself, it only reacts once Remote Config has already fetched something.")]
        [SerializeField] private float _remoteConfigPollSeconds = 60f;

        [SerializeField] private AudioClip _unlockSound;
        [Tooltip("How long (seconds) an unlocked achievement notification stays on screen before fading out.")]
        [SerializeField] private float _toastHoldDuration = 4.5f;

        [Tooltip("Shown instead of an achievement's own icon when it cannot be loaded (e.g. an achievement " +
            "added later purely through Remote Config, whose art was never packaged in this build). Leave " +
            "empty to use the package's plain generated placeholder badge.")]
        [SerializeField] private Sprite _fallbackIcon;

        [Header("Backend (auto-filled from Resources/PatreonConfig.asset when left empty)")]
        [SerializeField] private string _supabaseUrl = "";
        [SerializeField] private string _supabasePublishableKey = "";

        [Header("Launcher integration")]
        [Tooltip("Folder (under the OS per-user config directory) the launcher's shared settings file lives " +
            "in. Empty uses Application.companyName, which already matches a DryreL Hub launcher that does " +
            "the same on its side - no studio name needs to be hardcoded here.")]
        [SerializeField] private string _sharedSettingsFolder = "";

        [Tooltip("Resources.Load prefix for packaged achievement icons.")]
        [SerializeField] private string _iconResourcesPrefix = "Achievements/";

        private ExternalIdentitySessionSource _identity;
        private float _remoteConfigPollTimer;

        public static PatreonAchievementsBootstrap Instance { get; private set; }

        /// <summary>Creates the bootstrap object if one is not already present. Safe to call from anywhere.</summary>
        public static PatreonAchievementsBootstrap EnsureCreated()
        {
            if (Instance != null) return Instance;
            return new GameObject("PatreonAchievementsBootstrap").AddComponent<PatreonAchievementsBootstrap>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            if (Instance != null) return;
            var prefab = Resources.Load<PatreonAchievementsBootstrap>(""PatreonAchievementsBootstrap"");
            if (prefab != null)
            {
                Instantiate(prefab).name = ""PatreonAchievementsBootstrap"";
            }
            else
            {
                EnsureCreated();
            }
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

            if (_manifest == null && !string.IsNullOrEmpty(_manifestResourcePath))
            {
                _manifest = Resources.Load<TextAsset>(_manifestResourcePath);
                if (_manifest == null)
                    Debug.LogError("[Achievements] No manifest assigned and Resources/" + _manifestResourcePath + ".json was not found; achievements will be disabled.");
            }

            ResolveCredentialsFromPatreonConfig();
            if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabasePublishableKey))
            {
                Debug.LogWarning("[Achievements] No Supabase URL/publishable key (not set here and none found on " +
                    "Resources/PatreonConfig.asset); achievements will run local-only, without server sync.");
            }

            _identity = new ExternalIdentitySessionSource(
                string.IsNullOrEmpty(_supabaseUrl) ? "" : _supabaseUrl + MiddlewareSuffix + "?action=supabase-session",
                _supabasePublishableKey,
                UnityAchievementManager.Transport,
                hasIdentity: () => PatreonManager.Instance != null && PatreonManager.Instance.IsUserAuthenticated(),
                getIdentityToken: () => PatreonManager.Instance != null ? PatreonManager.Instance.GetValidAccessToken() : null,
                tokenFieldName: "patreon_access_token");

            // PatreonManager.PatreonUser is internal to that plugin's assembly, so the handler here must be a
            // lambda with an inferred parameter type rather than a named method with an explicit PatreonUser
            // parameter (which would not compile from outside that assembly).
            PatreonManager.OnUserAuthenticated += _ => _identity?.NotifyIdentityChanged();
            PatreonManager.OnUserSignedOut += OnPatreonUserSignedOut;

            var auth = new SupabaseSessionAuthProvider(_supabaseUrl, _supabasePublishableKey, UnityAchievementManager.Transport, _identity);
            string sharedSettingsFolder = string.IsNullOrEmpty(_sharedSettingsFolder) ? Application.companyName : _sharedSettingsFolder;

            UnityAchievementManager.Create(new UnityAchievementManager.Config
            {
                CatalogJson = _manifest,
                RemoteConfigKey = _remoteConfigKey,
                SupabaseUrl = _supabaseUrl,
                SupabasePublishableKey = _supabasePublishableKey,
                SharedSettingsFolder = sharedSettingsFolder,
                IconResourcesPrefix = _iconResourcesPrefix,
                FallbackIcon = _fallbackIcon,
                UnlockSound = _unlockSound,
                ToastHoldDuration = _toastHoldDuration,
                VerboseLogging = Debug.isDebugBuild,
            }, auth, CreateLocalizationProviderIfAvailable());
        }

        /// <summary>
        /// Fills <see cref="_supabaseUrl"/>/<see cref="_supabasePublishableKey"/> from the Unity Patreon
        /// Authenticator's own config asset when they were left blank in the Inspector, so the same
        /// project/key does not have to be entered twice. <c>PatreonConfig</c> lives under Resources, so it
        /// is reachable from anywhere without a direct reference to whatever object holds it in the scene.
        /// </summary>
        private void ResolveCredentialsFromPatreonConfig()
        {
            if (!string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_supabasePublishableKey)) return;

            var config = Resources.Load<PatreonConfig>(PatreonConfigResourcePath);
            if (config == null)
            {
                Debug.LogWarning("[Achievements] Resources/" + PatreonConfigResourcePath + ".asset not found; " +
                    "cannot auto-fill Supabase URL/key from it.");
                return;
            }

            if (string.IsNullOrEmpty(_supabasePublishableKey)) _supabasePublishableKey = config.SupabasePublishableKey;

            if (string.IsNullOrEmpty(_supabaseUrl))
            {
                string middleware = config.MiddlewareApiBaseUrl; // e.g. https://xxxx.supabase.co/functions/v1/patreon-middleware
                if (!string.IsNullOrEmpty(middleware) && middleware.EndsWith(MiddlewareSuffix, StringComparison.Ordinal))
                {
                    _supabaseUrl = middleware.Substring(0, middleware.Length - MiddlewareSuffix.Length);
                }
                else if (!string.IsNullOrEmpty(middleware))
                {
                    Debug.LogWarning("[Achievements] PatreonConfig.MiddlewareApiBaseUrl ('" + middleware +
                        "') does not end with '" + MiddlewareSuffix + "'; cannot derive the Supabase project URL from it. " +
                        "Set 'Supabase Url' explicitly on this component instead.");
                }
            }
        }

        /// <summary>
        /// Uses Unity Localization if it is installed, via reflection so this assembly does not need a
        /// hard, always-on reference to <c>DryreLHub.SupabaseGameAchievements.Unity.Localization</c> (which
        /// itself only exists when <c>com.unity.localization</c> is installed).
        /// </summary>
        private static IAchievementLocalizationProvider CreateLocalizationProviderIfAvailable()
        {
            try
            {
                var type = Type.GetType(UnityLocalizationProviderTypeName);
                return type != null ? Activator.CreateInstance(type) as IAchievementLocalizationProvider : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Achievements] Unity Localization was detected but could not be initialized; using fallback text. " + e.Message);
                return null;
            }
        }

        private void OnPatreonUserSignedOut() => _identity?.NotifyIdentityChanged();

        private void Update()
        {
            if (string.IsNullOrEmpty(_remoteConfigKey) || _remoteConfigPollSeconds <= 0f) return;
            _remoteConfigPollTimer += Time.unscaledDeltaTime;
            if (_remoteConfigPollTimer < _remoteConfigPollSeconds) return;
            _remoteConfigPollTimer = 0f;
            UnityAchievementManager.Instance?.RefreshCatalogFromRemoteConfig();
        }

        private void OnDestroy()
        {
            // OnUserAuthenticated is not unsubscribed here: its parameter type (PatreonManager.PatreonUser) is
            // internal to that plugin's assembly, so the handler above must stay an inferred-type lambda, which
            // means there is no delegate reference here to pass to -=. This is harmless in practice: both
            // PatreonManager and this bootstrap are DontDestroyOnLoad singletons that live for the app's
            // lifetime, so the subscription is never re-added and never outlives its target.
            PatreonManager.OnUserSignedOut -= OnPatreonUserSignedOut;
            if (Instance == this) Instance = null;
        }
    }
}


