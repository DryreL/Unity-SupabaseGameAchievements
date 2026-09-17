using DryreLHub.SupabaseGameAchievements.Patreon;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Patreon.Editor
{
    /// <summary>
    /// One-click setup: adds a <see cref="PatreonAchievementsBootstrap"/> to the open scene so a game does
    /// not need a hand-written bootstrap script at all. Configure the Supabase URL/key, manifest and Remote
    /// Config settings in the Inspector afterward (or leave them - Supabase URL/key auto-fill from
    /// <c>Resources/PatreonConfig.asset</c> if present).
    /// </summary>
    internal static class PatreonAchievementsBootstrapMenu
    {
        [MenuItem("Tools/DryreL Hub/Setup Achievements (Patreon) In Scene", priority = 100)]
        private static void SetupInScene()
        {
            var existing = Object.FindFirstObjectByType<PatreonAchievementsBootstrap>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("Achievements", "A PatreonAchievementsBootstrap already exists in this scene (" + existing.gameObject.name + "). Selecting it instead of creating another one.", "OK");
                return;
            }

            var go = new GameObject("PatreonAchievementsBootstrap");
            Undo.RegisterCreatedObjectUndo(go, "Setup Achievements");
            go.AddComponent<PatreonAchievementsBootstrap>();
            Selection.activeGameObject = go;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("Achievements",
                "Added a PatreonAchievementsBootstrap to the scene.\n\n" +
                "Assign a manifest (or leave it to auto-load from Resources/Achievements/achievements.json), " +
                "and check the Supabase URL/Key were auto-filled from PatreonConfig. Save the scene when ready.",
                "OK");
        }

        [MenuItem("Tools/DryreL Hub/Setup Achievements (Patreon) In Scene", true)]
        private static bool ValidateSetupInScene() => !EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
