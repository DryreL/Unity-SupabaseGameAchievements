using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// "Game setup": nothing starts the achievement system by itself, so without a bootstrap in the game's first
    /// scene (or a code call) every unlock and every rule is ignored. This shows whether one exists and adds it.
    /// </summary>
    public sealed partial class AchievementDashboardWindow
    {
        // What is saved in the project (the slow part, kept until a scene is saved or opened, or "Check again"),
        // and that plus the scenes open right now (rebuilt on every hierarchy change, so an unsaved bootstrap counts).
        private AchievementRuntimeSetup.Report _setupOnDisk;
        private AchievementRuntimeSetup.Report _setup;

        private void DrawGameSetup()
        {
            if (_setup == null)
            {
                if (_setupOnDisk == null) _setupOnDisk = AchievementRuntimeSetup.Scan();
                _setup = AchievementRuntimeSetup.Combine(_setupOnDisk);
            }

            // A local copy: "Check again" clears the field in the middle of this method, and the rest must still finish.
            var setup = _setup;
            if (setup.IsStarted)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    string where = string.Join(", ", setup.StartedBy.Take(3)) + (setup.StartedBy.Count > 3 ? " (+" + (setup.StartedBy.Count - 3) + " more)" : "");
                    GUILayout.Label(new GUIContent("Game setup: achievements are started by " + where + ".", string.Join("\n", setup.StartedBy)), _styles.Mini);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Check again", EditorStyles.miniButton, GUILayout.Width(84))) RecheckSetup();
                    GUILayout.Space(8);
                }
                if (setup.HasUnsaved)
                    EditorGUILayout.HelpBox("Save the scene (Ctrl+S): until then a build or another machine does not have the bootstrap.", MessageType.Info);

                var active = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                if (active.IsValid() && AchievementRuntimeSetup.SceneLacksStarter(setup, active.path))
                {
                    EditorGUILayout.HelpBox(
                        "The open scene '" + active.name + "' has no bootstrap; it is only in " + string.Join(", ", setup.BootstrapScenes) + ". " +
                        "Pressing Play in this scene (or loading it first) leaves achievements off, and every unlock and rule is ignored. " +
                        "Add one here too: it does nothing when the system is already running, so it is safe in every scene.",
                        MessageType.Warning);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(8);
                        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                        {
                            if (GUILayout.Button("Add bootstrap to this scene too", GUILayout.Height(22))) AddBootstrap();
                        }
                        GUILayout.Space(8);
                    }
                }
                return;
            }

            EditorGUILayout.HelpBox(
                "Game setup: nothing in this project starts the achievement system, so every unlock and every rule is ignored while the game runs. " +
                "Add an Achievement Bootstrap to the scene the game starts with (it keeps running across scene loads).",
                MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button(new GUIContent("Add bootstrap to the open scene", "Adds an AchievementBootstrap configured with this dashboard's manifest and Supabase URL."), GUILayout.Height(24)))
                        AddBootstrap();
                }

                if (AchievementRuntimeSetup.PatreonSampleImported() &&
                    GUILayout.Button(new GUIContent("Use the Patreon setup instead", "Adds the Patreon Integration sample's bootstrap, which also wires Patreon sign-in so unlocks sync."), GUILayout.Height(24)))
                {
                    EditorApplication.ExecuteMenuItem("Tools/DryreL Hub/Supabase Game Achievements/Setup Achievements (Patreon) In Scene");
                    RecheckSetup();
                }

                if (GUILayout.Button("Check again", GUILayout.Width(90), GUILayout.Height(24))) RecheckSetup();
                GUILayout.Space(8);
            }
        }

        private void AddBootstrap()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<TextAsset>(_data.ManifestPath);

            // Only a publishable key may go into a scene: it ships with the game. The service key never leaves this window.
            string publishableKey = IsReadOnlyKey ? _serviceKey : null;
            if (string.IsNullOrEmpty(publishableKey) && PatreonConfigReflection.TryGetSupabaseCredentials(out _, out string fromConfig)) publishableKey = fromConfig;
            if (!AchievementRuntimeSetup.IsPublicKey(publishableKey)) publishableKey = null;

            var result = AchievementRuntimeSetup.AddBootstrapToActiveScene(manifest, _supabaseUrl, publishableKey);
            string note = manifest == null ? " No manifest was assigned (write it with the Manifest button; the bootstrap also finds Resources/Achievements/achievements.json)." : "";
            SetStatus(result.Message + note, result.Ok ? MessageType.Info : MessageType.Warning);
            _setup = null; // the new object is in the open scene, so the next read sees it without touching the disk
        }

        private void RecheckSetup()
        {
            _setupOnDisk = null;
            _setup = null;
        }
    }
}
