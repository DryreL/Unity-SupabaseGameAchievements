using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Play Mode helper for exercising <see cref="AchievementManager"/> with buttons instead of writing
    /// throwaway test code: unlock/re-check any achievement in the current catalog, force a sync or a
    /// server reconciliation, and watch pending/unlocked counts update live.
    /// </summary>
    /// <remarks>
    /// Everything here goes through the same static <see cref="AchievementManager"/> facade gameplay code
    /// uses, so it only works once something in the scene has called <c>AchievementManager.Initialize</c>
    /// (e.g. <c>UnityAchievementManager</c>/<c>PatreonAchievementsBootstrap</c>) and only while playing.
    /// </remarks>
    public sealed class AchievementDebugWindow : EditorWindow
    {
        private Vector2 _scroll;
        private string _lastActionStatus = "";
        private bool _busy;

        [MenuItem("Tools/DryreL Hub/Supabase Game Achievements/Achievement Debug Window")]
        private static void Open() => GetWindow<AchievementDebugWindow>("Achievement Debug").minSize = new Vector2(420, 320);

        private void OnEnable() => EditorApplication.update += Repaint;

        private void OnDisable() => EditorApplication.update -= Repaint;

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to inspect and exercise achievements.", MessageType.Info);
                return;
            }

            if (!AchievementManager.IsInitialized)
            {
                EditorGUILayout.HelpBox("AchievementManager is not initialized yet in this scene.", MessageType.Warning);
                return;
            }

            var system = AchievementManager.Current;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(system.Catalog.GameSlug + " (catalog v" + system.Catalog.CatalogVersion + ")", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                GUILayout.Label("Unlocked " + system.UnlockedCount + "/" + system.Catalog.Count, EditorStyles.toolbarButton);
                GUILayout.Label("Pending " + system.PendingSyncCount, EditorStyles.toolbarButton);
                GUILayout.Label(system.IsSyncing ? "Syncing..." : "Idle", EditorStyles.toolbarButton);
            }

            using (new EditorGUI.DisabledScope(_busy))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sync Now")) RunAsync("Sync", system.SyncAsync());
                if (GUILayout.Button("Reconcile From Server")) RunAsync("Reconcile", system.SyncServerStateAsync());
            }

            if (!string.IsNullOrEmpty(_lastActionStatus)) EditorGUILayout.HelpBox(_lastActionStatus, MessageType.None);

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var definition in system.GetDefinitions())
            {
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    bool unlocked = system.HasUnlocked(definition.Key);
                    bool pending = system.IsPendingSync(definition.Key);

                    EditorGUILayout.LabelField(unlocked ? "✓" : "–", GUILayout.Width(16));
                    EditorGUILayout.LabelField(new GUIContent(definition.Key, definition.Title + " - " + definition.Description), GUILayout.Width(160));
                    EditorGUILayout.LabelField(definition.Title, GUILayout.MinWidth(80));
                    if (definition.IsRetired) EditorGUILayout.LabelField("retired", GUILayout.Width(50));
                    else if (pending) EditorGUILayout.LabelField("pending sync", GUILayout.Width(80));

                    using (new EditorGUI.DisabledScope(unlocked || definition.IsRetired))
                    {
                        if (GUILayout.Button("Unlock", GUILayout.Width(70)))
                        {
                            bool changed = AchievementManager.TryUnlock(definition.Key);
                            _lastActionStatus = changed ? "Unlocked '" + definition.Key + "'." : "'" + definition.Key + "' was already unlocked or is unknown.";
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private async void RunAsync(string label, Task<AchievementSyncResult> task)
        {
            _busy = true;
            _lastActionStatus = label + "...";
            try
            {
                var result = await task;
                _lastActionStatus = label + ": " + result.Status +
                    (result.Cleared > 0 ? ", cleared " + result.Cleared : "") +
                    (result.Rejected > 0 ? ", rejected " + result.Rejected : "") +
                    (result.Merged > 0 ? ", merged " + result.Merged : "") +
                    (!string.IsNullOrEmpty(result.Error) ? " (" + result.Error + ")" : "");
            }
            catch (Exception e)
            {
                _lastActionStatus = label + " threw: " + e.Message;
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }
    }
}
