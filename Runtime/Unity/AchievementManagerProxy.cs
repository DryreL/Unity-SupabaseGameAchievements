using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Safe scene proxy and lazy-initializer for <see cref="AchievementManager"/>.
    /// <para>
    /// Guarantees that the achievement system is properly initialized before any calls are executed.
    /// If a scene is loaded directly in the editor (e.g. Play mode on a level without running MainMenu first),
    /// this proxy automatically finds or instantiates the bootstrap prefab or creates a runtime fallback host
    /// so achievement unlocks are never lost.
    /// </para>
    /// <para>
    /// Can be used as a static facade (<c>AchievementManagerProxy.TryUnlock("key")</c>) or attached
    /// as a <see cref="MonoBehaviour"/> to hook up Unity UI buttons, trigger volumes, animation events, etc.
    /// </para>
    /// </summary>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Manager Proxy")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-950)]
    public sealed class AchievementManagerProxy : MonoBehaviour
    {
        [Header("Auto Initialization")]
        [Tooltip("When true, automatically checks and initializes the achievement system during Awake if not already running.")]
        [SerializeField] private bool _autoInitialize = true;

        [Tooltip("Optional custom bootstrap prefab path in Resources (without file extension) to load if no active manager is found.")]
        [SerializeField] private string _customBootstrapResourcePath = "Achievements/PatreonAchievementsBootstrap";

        [Header("Unity Events (Optional)")]
        [Tooltip("Fires whenever any achievement is unlocked while this component is active.")]
        [SerializeField] private UnityEvent<string> _onAchievementUnlocked;

        public static AchievementManagerProxy Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            if (_autoInitialize)
            {
                EnsureInitialized(_customBootstrapResourcePath);
            }
        }

        private void OnEnable()
        {
            AchievementManager.AchievementUnlocked += HandleAchievementUnlocked;
        }

        private void OnDisable()
        {
            AchievementManager.AchievementUnlocked -= HandleAchievementUnlocked;
        }

        private void HandleAchievementUnlocked(AchievementUnlockedEvent evt)
        {
            _onAchievementUnlocked?.Invoke(evt.Definition.Key);
        }

        // ==========================================
        // Safe Initialization Engine
        // ==========================================

        /// <summary>
        /// Guarantees that the achievement system is initialized.
        /// Searches for existing objects in the scene, loads from Resources prefab if available,
        /// invokes Patreon bootstrap if present, or creates a runtime fallback host.
        /// </summary>
        public static void EnsureInitialized(string customResourcePath = null)
        {
            if (AchievementManager.IsInitialized && UnityAchievementManager.Instance != null)
            {
                return;
            }

            // 1. Check if an existing UnityAchievementManager exists in the scene (even if inactive)
            var existingManager = UnityEngine.Object.FindFirstObjectByType<UnityAchievementManager>(FindObjectsInactive.Include);
            if (existingManager != null)
            {
                if (!existingManager.gameObject.activeSelf)
                {
                    existingManager.gameObject.SetActive(true);
                }
                return;
            }

            // 2. Try loading a bootstrap Prefab from Resources
            string[] searchPaths = string.IsNullOrEmpty(customResourcePath)
                ? new[]
                {
                    "Achievements/PatreonAchievementsBootstrap",
                    "Achievements/AchievementBootstrap",
                    "PatreonAchievementsBootstrap",
                    "AchievementBootstrap"
                }
                : new[]
                {
                    customResourcePath,
                    "Achievements/PatreonAchievementsBootstrap",
                    "Achievements/AchievementBootstrap"
                };

            foreach (var path in searchPaths)
            {
                var prefab = Resources.Load<GameObject>(path);
                if (prefab != null)
                {
                    UnityEngine.Object.Instantiate(prefab);
                    return;
                }
            }

            // 3. Try to call PatreonAchievementsBootstrap.EnsureCreated() via reflection if present
            try
            {
                var patreonBootstrapType = Type.GetType("DryreLHub.SupabaseGameAchievements.Patreon.PatreonAchievementsBootstrap, DryreLHub.SupabaseGameAchievements.Patreon")
                    ?? Type.GetType("DryreLHub.SupabaseGameAchievements.Patreon.PatreonAchievementsBootstrap, Assembly-CSharp");

                if (patreonBootstrapType != null)
                {
                    var ensureMethod = patreonBootstrapType.GetMethod("EnsureCreated", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (ensureMethod != null)
                    {
                        ensureMethod.Invoke(null, null);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AchievementManagerProxy] Reflective bootstrap call failed: {ex.Message}");
            }

            // 4. Fallback: Automatically create UnityAchievementManager directly from bundled manifest
            var defaultManifest = Resources.Load<TextAsset>("Achievements/achievements");
            if (defaultManifest != null)
            {
                UnityAchievementManager.Create(new UnityAchievementManager.Config
                {
                    CatalogJson = defaultManifest,
                    IconResourcesPrefix = "Achievements/",
                    SharedSettingsFolder = Application.companyName,
                    VerboseLogging = Debug.isDebugBuild
                });
                return;
            }

            Debug.LogWarning("[AchievementManagerProxy] Could not automatically initialize achievement system. Please ensure a bootstrap prefab exists in Resources or manifest exists at 'Resources/Achievements/achievements.json'.");
        }

        // ==========================================
        // Static Facade API (Code-First Access)
        // ==========================================

        /// <summary>
        /// Attempts to unlock an achievement safely. Automatically initializes the system if not ready.
        /// </summary>
        public static bool TryUnlock(string achievementKey)
        {
            EnsureInitialized();
            return AchievementManager.TryUnlock(achievementKey);
        }

        /// <summary>
        /// Checks whether the achievement is unlocked. Automatically initializes the system if not ready.
        /// </summary>
        public static bool HasUnlocked(string achievementKey)
        {
            EnsureInitialized();
            return AchievementManager.HasUnlocked(achievementKey);
        }

        /// <summary>
        /// Gets the definition of a specific achievement.
        /// </summary>
        public static AchievementDefinition GetDefinition(string achievementKey)
        {
            EnsureInitialized();
            return AchievementManager.GetDefinition(achievementKey);
        }

        /// <summary>
        /// Gets all defined achievements in the current catalog.
        /// </summary>
        public static IReadOnlyList<AchievementDefinition> GetDefinitions()
        {
            EnsureInitialized();
            return AchievementManager.GetDefinitions();
        }

        /// <summary>
        /// Triggers a synchronization with the backend.
        /// </summary>
        public static Task<AchievementSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            return AchievementManager.SyncAsync(cancellationToken);
        }

        // ==========================================
        // Instance / Inspector API (UI & UnityEvents)
        // ==========================================

        /// <summary>
        /// Unlocks an achievement. Designed for Unity UI Button OnClick() and UnityEvents.
        /// </summary>
        public void Unlock(string achievementKey)
        {
            TryUnlock(achievementKey);
        }

        /// <summary>
        /// Triggers synchronization. Designed for Unity UI Button OnClick() and UnityEvents.
        /// </summary>
        public void Sync()
        {
            _ = SyncAsync();
        }
    }
}
