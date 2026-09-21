using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// The Debug tab (Play Mode): exercise the running game's achievements with buttons instead of throwaway
    /// test code. Everything goes through the same static <see cref="AchievementManager"/> facade gameplay uses,
    /// so it needs something in the scene to have initialized it (<c>UnityAchievementManager</c> / a bootstrap).
    /// </summary>
    public sealed partial class AchievementDashboardWindow
    {
        private Vector2 _debugScroll;
        private string _debugStatus = "";
        private bool _debugBusy;

        private void DrawDebugTab()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to unlock achievements, force a sync or fire rule events in the running game.\n\nYou can reset the local save at any time.", MessageType.Info);
                DrawClearLocalButton();
                return;
            }

            if (!AchievementManager.IsInitialized)
            {
                EditorGUILayout.HelpBox("AchievementManager is not initialized yet in this scene.", MessageType.Warning);
                DrawClearLocalButton();
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

            using (new EditorGUI.DisabledScope(_debugBusy))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sync Now")) RunDebugAsync("Sync", system.SyncAsync());
                if (GUILayout.Button("Reconcile From Server")) RunDebugAsync("Reconcile", system.SyncServerStateAsync());
                if (GUILayout.Button("Clear Local State")) ClearLocalAchievements();
            }

            if (!string.IsNullOrEmpty(_debugStatus)) EditorGUILayout.HelpBox(_debugStatus, MessageType.None);

            EditorGUILayout.Space(4);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_debugScroll))
            {
                _debugScroll = scroll.scrollPosition;

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
                                _debugStatus = changed ? "Unlocked '" + definition.Key + "'." : "'" + definition.Key + "' was already unlocked or is unknown.";
                            }
                        }
                    }
                }

                DrawDebugRules();
            }
        }

        // The dashboard's rules, testable without clicking through the game: fire an event, watch a counter.
        private void DrawDebugRules()
        {
            var rules = _data.Rules.Where(r => _data.ValidateRule(r).Count == 0).ToList();
            if (rules.Count == 0) return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Rules", _styles.Section);

            var runner = UnityEngine.Object.FindFirstObjectByType<AchievementRulesRunner>();
            if (runner == null || runner.Rules == null)
            {
                EditorGUILayout.HelpBox("The dashboard's rules are not loaded in the running game. Write rules.json from the Rules tab (it must be under a Resources folder) and enter Play Mode again.", MessageType.Info);
                return;
            }

            foreach (var rule in rules)
            {
                var achievement = _data.Achievements.FirstOrDefault(a => a.Key == rule.AchievementKey);
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    string title = achievement != null && !string.IsNullOrEmpty(achievement.Title) ? achievement.Title : rule.AchievementKey;
                    EditorGUILayout.LabelField(new GUIContent(title, "Event: " + rule.EventName(rule.Roles[0])), GUILayout.MinWidth(120));

                    if (rule.Kind == AchievementRuleKind.Counter)
                    {
                        var progress = runner.Rules.GetProgress(rule.AchievementKey);
                        EditorGUILayout.LabelField(progress.ToString(), GUILayout.Width(56));
                        if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.Width(46))) runner.Rules.ResetProgress(rule.AchievementKey);
                    }

                    foreach (string role in rule.Roles)
                    {
                        string label = role == "trigger" ? "Fire" : char.ToUpperInvariant(role[0]) + role.Substring(1);
                        if (GUILayout.Button(new GUIContent(label, "Report '" + rule.EventName(role) + "'"), EditorStyles.miniButton, GUILayout.Width(role == "trigger" ? 46 : 62)))
                        {
                            AchievementEvents.Report(rule.EventName(role));
                            _debugStatus = "Reported '" + rule.EventName(role) + "'.";
                        }
                    }
                }
            }
        }

        private void DrawClearLocalButton()
        {
            if (GUILayout.Button("Clear Local Achievements From Disk", GUILayout.Height(28))) ClearLocalAchievements();
        }

        /// <summary>Deletes the on-disk save under <c>persistentDataPath/achievements</c> and resets the running manager, if any.</summary>
        private static void ClearLocalAchievements()
        {
            string dir = Path.Combine(Application.persistentDataPath, "achievements");
            bool confirm = EditorUtility.DisplayDialog(
                "Clear Local Achievements",
                "Are you sure you want to delete all locally stored achievements on disk?\n\nStorage Path:\n" + dir + "\n\nThis will reset all unlocked and pending status.",
                "Yes, Reset",
                "Cancel");
            if (!confirm) return;

            int deletedFiles = 0;
            if (Directory.Exists(dir))
            {
                deletedFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[Achievements] Failed to delete achievements directory: " + ex.Message);
                }
            }

            if (Application.isPlaying && UnityAchievementManager.Instance != null) UnityAchievementManager.Instance.ResetLocalState();

            Debug.Log("[Achievements] Local achievement storage cleared successfully. (" + deletedFiles + " file(s) deleted from " + dir + ")");
            EditorUtility.DisplayDialog("Achievements Cleared", "Local achievements have been reset successfully!\n\nDeleted " + deletedFiles + " file(s) from:\n" + dir, "OK");
        }

        private async void RunDebugAsync(string label, Task<AchievementSyncResult> task)
        {
            _debugBusy = true;
            _debugStatus = label + "...";
            try
            {
                var result = await task;
                _debugStatus = label + ": " + result.Status +
                    (result.Cleared > 0 ? ", cleared " + result.Cleared : "") +
                    (result.Rejected > 0 ? ", rejected " + result.Rejected : "") +
                    (result.Merged > 0 ? ", merged " + result.Merged : "") +
                    (!string.IsNullOrEmpty(result.Error) ? " (" + result.Error + ")" : "");
            }
            catch (Exception e)
            {
                _debugStatus = label + " threw: " + e.Message;
            }
            finally
            {
                _debugBusy = false;
                Repaint();
            }
        }
    }
}
