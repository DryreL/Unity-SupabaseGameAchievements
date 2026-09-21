using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Catalog maintenance that used to live in separate windows: importing an <c>achievements.json</c>, generating an
    /// SQL restore script, and permanently deleting a retired achievement.
    /// </summary>
    public sealed partial class AchievementDashboardWindow
    {
        private DashboardAchievement _deleteTarget;
        private string _deleteConfirmation = "";

        // =====================================================================================================
        // Import / SQL menu
        // =====================================================================================================

        private void ShowFileMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Import manifest (achievements.json)..."), false, ImportManifestFile);
            menu.AddSeparator("");

            bool canSql = _data.GameId > 0 && _data.Achievements.Any(a => a.Id > 0);
            if (canSql)
            {
                menu.AddItem(new GUIContent("Copy import SQL"), false, CopyImportSql);
                menu.AddItem(new GUIContent("Save import SQL..."), false, SaveImportSql);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Copy import SQL (connect and push first)"));
                menu.AddDisabledItem(new GUIContent("Save import SQL... (connect and push first)"));
            }
            menu.DropDown(buttonRect);
        }

        private void ImportManifestFile()
        {
            string start = Path.GetDirectoryName(ToFullPath(string.IsNullOrEmpty(_data.ManifestPath) ? DashboardData.DefaultManifestPath : _data.ManifestPath));
            string chosen = EditorUtility.OpenFilePanel("Import achievements.json into the dashboard", start, "json");
            if (string.IsNullOrEmpty(chosen)) return;

            try
            {
                var manifest = JObject.Parse(File.ReadAllText(chosen, Encoding.UTF8));
                var result = _data.ImportManifest(manifest);
                MarkDirty();

                string message = "Imported " + Path.GetFileName(chosen) + ": " + result.Added + " added, " + result.Updated + " updated, " + result.Unchanged + " unchanged.";
                if (result.Skipped.Count > 0) message += " Skipped: " + string.Join("; ", result.Skipped) + ".";
                message += " New entries are drafts: Connect & Pull links the ones Supabase already has, Push sends the rest.";
                SetStatus(message, result.Skipped.Count > 0 ? MessageType.Warning : MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Could not import '" + chosen + "': " + e.Message, MessageType.Error);
            }
        }

        private string BuildImportSql()
        {
            var achievements = (JArray)_data.BuildManifest()["achievements"];
            return AchievementCatalogImporter.BuildImportSql(_data.GameId, _data.GameSlug, _data.GameName, _data.CatalogVersion, achievements);
        }

        private void CopyImportSql()
        {
            try
            {
                GUIUtility.systemCopyBuffer = BuildImportSql();
                SetStatus("Import SQL copied to the clipboard. Paste it into the Supabase SQL editor: it restores every achievement with its id (safe to run twice) and re-syncs the id sequence.", MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Could not build the SQL: " + e.Message, MessageType.Error);
            }
        }

        private void SaveImportSql()
        {
            string path = EditorUtility.SaveFilePanel("Save import SQL", ProjectRoot, "import_achievements.sql", "sql");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, BuildImportSql(), new UTF8Encoding(false));
                SetStatus("Saved the import SQL to " + path + ".", MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Could not save the SQL: " + e.Message, MessageType.Error);
            }
        }

        // =====================================================================================================
        // Permanent delete (a retired achievement nobody ever unlocked)
        // =====================================================================================================

        private void DrawDangerZone(DashboardAchievement a)
        {
            if (a.Id <= 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Danger zone", _styles.Section);

            bool eligible = a.Retired && a.State == DashboardSyncState.Synced;
            using (new EditorGUI.DisabledScope(!eligible || !CanWrite(out _)))
            {
                if (GUILayout.Button(new GUIContent("Delete permanently from Supabase...", "Only for a mistake that never shipped. The server refuses if any player unlocked it."), GUILayout.Height(22)))
                {
                    _deleteTarget = a;
                    _deleteConfirmation = "";
                    GUI.FocusControl(null);
                }
            }

            string hint = !a.Retired
                ? "Tick Retired and push first: the server only deletes a retired achievement. Retiring is the normal way to remove one."
                : !eligible
                    ? "Push the retirement first: the server only deletes an achievement that is already retired there."
                    : "Frees its bit index for reuse. The server refuses if any player has unlocked it.";
            EditorGUILayout.LabelField(hint, _styles.Mini);
        }

        private void DrawDeleteConfirmation()
        {
            if (_deleteTarget == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.HelpBox(
                    "This permanently deletes '" + _deleteTarget.Key + "' from Supabase and frees its bit index for reuse. " +
                    "Only do this for a mistake that never shipped; the server refuses if any player has unlocked it. Type the achievement key to confirm.",
                    MessageType.Warning);
                _deleteConfirmation = EditorGUILayout.TextField("Type '" + _deleteTarget.Key + "' to confirm", _deleteConfirmation);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_isBusy || _deleteConfirmation != _deleteTarget.Key || !CanWrite(out _)))
                    {
                        if (GUILayout.Button("Delete Permanently"))
                        {
                            var target = _deleteTarget;
                            _deleteTarget = null;
                            DeleteRetired(target);
                            GUIUtility.ExitGUI();
                        }
                    }
                    if (GUILayout.Button("Cancel")) _deleteTarget = null;
                }
            }
        }

        private void DeleteRetired(DashboardAchievement achievement)
        {
            if (!CanWrite(out string problem)) { Fail(problem); return; }

            _isBusy = true;
            SetStatus("Deleting '" + achievement.Key + "'...", MessageType.Info);
            var body = new JObject { ["p_achievement_id"] = achievement.Id };
            Send("POST", "rpc/delete_retired_achievement", body.ToString(Newtonsoft.Json.Formatting.None), null, response =>
            {
                if (!response.Ok) { Fail(Describe(response)); return; }

                _data.Achievements.Remove(achievement);
                MarkDirty();
                SaveNow();
                RefreshCatalogVersion(() =>
                {
                    _isBusy = false;
                    SetStatus("Deleted '" + achievement.Key + "' from Supabase. Catalog is now v" + _data.CatalogVersion + ".", MessageType.Info);
                });
            });
        }
    }
}
