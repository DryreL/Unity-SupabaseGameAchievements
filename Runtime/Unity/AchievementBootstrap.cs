using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// The one object a game needs in its first scene to start the achievement system: nothing else starts it, so
    /// without something like this (or a code call to <see cref="UnityAchievementManager.Create"/>, or the Patreon
    /// sample's bootstrap) every <c>TryUnlock</c> and every rule is silently ignored. The Achievement Dashboard adds it for you
    /// (<i>Game setup</i>).
    /// </summary>
    /// <remarks>
    /// It creates the persistent <see cref="UnityAchievementManager"/> from the bundled manifest and does nothing when
    /// something else already started the system, so it is safe next to another bootstrap (it runs after them). It has
    /// no sign-in: unlocks are saved locally and sync once your sign-in code supplies an auth provider with
    /// <c>UnityAchievementManager.Instance.Initialize(provider)</c>; for Patreon games the Patreon Integration sample's
    /// bootstrap does that wiring instead of this component. The rules the dashboard authored
    /// (<c>Resources/Achievements/rules.json</c>) start with the manager, no extra setup.
    /// </remarks>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Bootstrap")]
    [DefaultExecutionOrder(100)] // after the Patreon sample's bootstrap (-900) and the manager itself (-1000)
    [DisallowMultipleComponent]
    public sealed class AchievementBootstrap : MonoBehaviour
    {
        [Tooltip("The manifest exported by the Achievement Dashboard. Empty = load Resources/<Manifest Resource Path>.")]
        [SerializeField] private TextAsset _manifest;
        [SerializeField] private string _manifestResourcePath = "Achievements/achievements";

        [Header("Backend (leave empty for local-only achievements)")]
        [SerializeField] private string _supabaseUrl = "";
        [Tooltip("Public publishable/anon key. Never a service role key.")]
        [SerializeField] private string _supabasePublishableKey = "";

        [Header("Icons and notifications")]
        [Tooltip("Prefix for Resources.Load of achievement icons (the dashboard's icon folder is relative to it).")]
        [SerializeField] private string _iconResourcesPrefix = "Achievements/";
        [Tooltip("Folder (under the OS per-user config directory) containing the launcher's shared settings file. Empty = notifications always on.")]
        [SerializeField] private string _sharedSettingsFolder = "";
        [SerializeField] private bool _verboseLogging;

        private void Awake()
        {
            if (UnityAchievementManager.Instance != null || AchievementManager.IsInitialized)
            {
                Destroy(this); // something else already started the system (another bootstrap, or an earlier scene)
                return;
            }

            var manifest = _manifest != null ? _manifest : Resources.Load<TextAsset>(_manifestResourcePath);
            if (manifest == null)
            {
                Debug.LogError("[Achievements] AchievementBootstrap has no manifest: assign one, or write it from the Achievement Dashboard to Resources/" + _manifestResourcePath + ".json. Achievements stay off.", this);
                return;
            }

            UnityAchievementManager.Create(new UnityAchievementManager.Config
            {
                CatalogJson = manifest,
                SupabaseUrl = _supabaseUrl,
                SupabasePublishableKey = _supabasePublishableKey,
                IconResourcesPrefix = _iconResourcesPrefix,
                SharedSettingsFolder = _sharedSettingsFolder,
                VerboseLogging = _verboseLogging,
            });
        }
    }
}
