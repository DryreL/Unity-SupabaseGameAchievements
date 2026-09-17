using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Samples
{
    /// <summary>
    /// Put this on an object in the first scene. It is the only place that knows about the backend, the
    /// sign-in system and the launcher; gameplay code just calls <see cref="AchievementManager"/>.
    /// </summary>
    public sealed class ExampleAchievementBootstrap : MonoBehaviour
    {
        [SerializeField] private TextAsset _manifest;
        [SerializeField] private AudioClip _unlockSound;

        [Header("Backend")]
        [SerializeField] private string _supabaseUrl = "https://YOUR-PROJECT.supabase.co";
        [SerializeField] private string _supabasePublishableKey = "sb_publishable_...";
        [SerializeField] private string _sessionEndpoint = "https://YOUR-PROJECT.supabase.co/functions/v1/patreon-middleware?action=supabase-session";
        [SerializeField] private string _identityTokenField = "patreon_access_token";

        [Header("Launcher integration")]
        [Tooltip("Must match the launcher's shared settings folder, e.g. the company folder under %APPDATA%.")]
        [SerializeField] private string _sharedSettingsFolder = "Your Company";

        private ExternalIdentitySessionSource _identity;

        private void Awake()
        {
            // Replace these two delegates with your sign-in system. For the Unity Patreon Authenticator:
            //   hasIdentity:      () => PatreonManager.Instance != null && PatreonManager.Instance.IsUserAuthenticated()
            //   getIdentityToken: () => PatreonManager.Instance != null ? PatreonManager.Instance.GetValidAccessToken() : null
            // and call _identity.NotifyIdentityChanged() from PatreonManager.OnUserAuthenticated / OnUserSignedOut.
            _identity = new ExternalIdentitySessionSource(
                _sessionEndpoint,
                _supabasePublishableKey,
                UnityAchievementManager.Transport,
                hasIdentity: () => false,
                getIdentityToken: () => null,
                tokenFieldName: _identityTokenField);

            var auth = new SupabaseSessionAuthProvider(_supabaseUrl, _supabasePublishableKey, UnityAchievementManager.Transport, _identity);

            IAchievementLocalizationProvider localization = null;
#if ACHIEVEMENTS_UNITY_LOCALIZATION
            localization = new UnityLocalizationProvider();
#endif

            UnityAchievementManager.Create(new UnityAchievementManager.Config
            {
                CatalogJson = _manifest,
                SupabaseUrl = _supabaseUrl,
                SupabasePublishableKey = _supabasePublishableKey,
                SharedSettingsFolder = _sharedSettingsFolder,
                UnlockSound = _unlockSound,
                VerboseLogging = Debug.isDebugBuild,
            }, auth, localization);
        }

        /// <summary>Wire this to your sign-in system's "signed in" / "signed out" callbacks.</summary>
        public void OnIdentityChanged() => _identity?.NotifyIdentityChanged();
    }
}

