using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization.Tables;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Plugs Unity Localization into the dashboard's Localize button. Compiled only when
    /// <c>com.unity.localization</c> is installed (see the asmdef's version define), so the rest of the
    /// Editor tooling never depends on it.
    /// </summary>
    [InitializeOnLoad]
    internal static class AchievementLocalizationEditor
    {
        static AchievementLocalizationEditor()
        {
            AchievementLocalizationBridge.Sync = Sync;
        }

        private static LocalizationSyncResult Sync(string folder, IReadOnlyList<LocalizationEntryPlan> plan)
        {
            if (LocalizationEditorSettings.ActiveLocalizationSettings == null)
                return LocalizationSyncResult.Fail(
                    "This project has no Localization Settings asset yet. Create one under Project Settings > Localization, then try again.");

            var locales = LocalizationEditorSettings.GetLocales();
            if (locales.Count == 0)
                return LocalizationSyncResult.Fail(
                    "This project has no Locales yet. Add at least one under Project Settings > Localization > Locale Generator, then try again.");

            var result = new LocalizationSyncResult { Ok = true, Locales = locales.Count };
            string root = folder.Trim().Replace('\\', '/').Trim('/');

            try
            {
                foreach (var group in plan.GroupBy(entry => entry.Table))
                {
                    var collection = LocalizationEditorSettings.GetStringTableCollections()
                        .FirstOrDefault(c => c.TableCollectionName == group.Key);

                    if (collection == null)
                    {
                        // Creates the asset folder too; the collection registers itself with Localization Settings.
                        collection = LocalizationEditorSettings.CreateStringTableCollection(group.Key, root + "/" + group.Key, locales.ToList());
                        result.TablesCreated++;
                    }

                    var tables = collection.StringTables.Where(table => table != null).ToList();
                    foreach (var entry in group)
                    {
                        // A key counts as "kept" if the collection already knows it, whatever its text is:
                        // existing text belongs to whoever translated it and is never overwritten.
                        bool existed = collection.SharedData.GetEntry(entry.Key) != null;
                        if (existed) result.EntriesKept++; else result.EntriesAdded++;

                        foreach (var table in tables)
                        {
                            if (table.GetEntry(entry.Key) != null) continue;
                            table.AddEntry(entry.Key, entry.Text);
                            EditorUtility.SetDirty(table);
                        }
                    }
                    EditorUtility.SetDirty(collection.SharedData);
                }

                AssetDatabase.SaveAssets();
            }
            catch (Exception e)
            {
                return LocalizationSyncResult.Fail("Unity Localization refused the change: " + e.Message);
            }

            return result;
        }
    }
}
