using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Finds out whether anything in the project starts the achievement system (nothing does by itself: without a
    /// bootstrap object, or a code call, every unlock and every rule is ignored), and adds the bootstrap to a scene.
    /// </summary>
    internal static class AchievementRuntimeSetup
    {
        internal sealed class Report
        {
            /// <summary>Where a bootstrap was found, e.g. "PatreonAchievementsBootstrap in MainMenu.unity".</summary>
            public readonly List<string> StartedBy = new List<string>();

            public bool IsStarted => StartedBy.Count > 0;
        }

        // Classes that start the system when they are in a scene or prefab, with the name shown to the user.
        private static readonly (string FullName, string Label)[] Bootstraps =
        {
            ("DryreLHub.SupabaseGameAchievements.Unity.AchievementBootstrap", "AchievementBootstrap"),
            ("DryreLHub.SupabaseGameAchievements.Patreon.PatreonAchievementsBootstrap", "PatreonAchievementsBootstrap"),
            ("DryreLHub.SupabaseGameAchievements.Unity.AchievementManagerProxy", "AchievementManagerProxy"),
        };

        private const long MaxFileBytes = 40L * 1024 * 1024;

        // ---- pure text checks (unit-tested) -----------------------------------------------------------------

        /// <summary>A scene or prefab uses a script when its YAML refers to the script's GUID.</summary>
        internal static bool ReferencesScript(string yaml, string scriptGuid) =>
            !string.IsNullOrEmpty(yaml) && !string.IsNullOrEmpty(scriptGuid) && yaml.Contains("guid: " + scriptGuid);

        /// <summary>Game code that starts the system by itself.</summary>
        internal static bool CodeStartsSystem(string source) =>
            !string.IsNullOrEmpty(source) &&
            (source.Contains("UnityAchievementManager.Create(") || source.Contains("AchievementManager.Initialize("));

        /// <summary>
        /// True for a key that is safe to ship in a scene: a <c>sb_publishable_</c> key, or a legacy JWT whose role is
        /// <c>anon</c>. A service/secret key (or anything unrecognised) is never accepted.
        /// </summary>
        internal static bool IsPublicKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith("sb_publishable_", StringComparison.Ordinal)) return true;
            if (!key.StartsWith("eyJ", StringComparison.Ordinal)) return false;

            string[] parts = key.Split('.');
            if (parts.Length < 2) return false;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return json.Replace(" ", "").Contains("\"role\":\"anon\"");
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ---- scanning ---------------------------------------------------------------------------------------

        /// <summary>Reads every scene, prefab and runtime script under Assets once. Small projects take well under a second.</summary>
        public static Report Scan()
        {
            var report = new Report();

            var scripts = new List<(string Guid, string Label)>();
            foreach ((string fullName, string label) in Bootstraps)
            {
                string simpleName = fullName.Substring(fullName.LastIndexOf('.') + 1);
                foreach (string guid in AssetDatabase.FindAssets(simpleName + " t:MonoScript"))
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                    if (script != null && script.GetClass() != null && script.GetClass().FullName == fullName) scripts.Add((guid, label));
                }
            }

            if (scripts.Count > 0)
            {
                foreach (string type in new[] { "t:Scene", "t:Prefab" })
                {
                    foreach (string guid in AssetDatabase.FindAssets(type, new[] { "Assets" }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        string yaml = ReadSmallText(path);
                        if (yaml == null || !yaml.StartsWith("%YAML", StringComparison.Ordinal)) continue;

                        foreach (var script in scripts)
                            if (ReferencesScript(yaml, script.Guid)) report.StartedBy.Add(script.Label + " in " + Path.GetFileName(path));
                    }
                }
            }

            foreach (string file in EnumerateRuntimeScripts())
            {
                string source = ReadSmallText(file);
                if (CodeStartsSystem(source)) report.StartedBy.Add("code in " + Path.GetFileName(file));
            }

            report.StartedBy.Sort(StringComparer.Ordinal);
            return report;
        }

        private static IEnumerable<string> EnumerateRuntimeScripts()
        {
            string assets = Application.dataPath;
            foreach (string file in Directory.EnumerateFiles(assets, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/Editor/")) continue; // editor code never runs in the game
                yield return file;
            }
        }

        private static string ReadSmallText(string path)
        {
            try
            {
                string full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path));
                var info = new FileInfo(full);
                return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(full) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when the Patreon Integration sample is imported, so its one-click setup exists.</summary>
        public static bool PatreonSampleImported() =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("DryreLHub.SupabaseGameAchievements.Patreon.PatreonAchievementsBootstrap", false) != null);

        // ---- adding the bootstrap ---------------------------------------------------------------------------

        /// <summary>Adds an <see cref="AchievementBootstrap"/> to the active scene. Returns a message for the user and whether it worked.</summary>
        public static (bool Ok, string Message) AddBootstrapToActiveScene(TextAsset manifest, string supabaseUrl, string publishableKey)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return (false, "Leave Play Mode first: a scene change made while playing is lost.");

            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return (false, "Open a scene first.");

            var existing = Object.FindFirstObjectByType<AchievementBootstrap>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return (true, "There is already an AchievementBootstrap in '" + existing.gameObject.scene.name + "' (" + existing.gameObject.name + "). Selected it.");
            }

            var go = new GameObject("AchievementBootstrap");
            Undo.RegisterCreatedObjectUndo(go, "Add Achievement Bootstrap");
            var bootstrap = go.AddComponent<AchievementBootstrap>();

            var serialized = new SerializedObject(bootstrap);
            if (manifest != null) serialized.FindProperty("_manifest").objectReferenceValue = manifest;
            if (!string.IsNullOrEmpty(supabaseUrl)) serialized.FindProperty("_supabaseUrl").stringValue = supabaseUrl;
            if (!string.IsNullOrEmpty(publishableKey)) serialized.FindProperty("_supabasePublishableKey").stringValue = publishableKey;
            serialized.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = go;
            return (true, "Added an AchievementBootstrap to '" + scene.name + "'. Put it in the scene the game starts with (it keeps running across scene loads), then save the scene.");
        }
    }
}
