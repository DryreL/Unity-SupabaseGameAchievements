using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
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

            // A project can list one locale twice (a duplicated Addressables entry); each copy would get its own table asset.
            var all = LocalizationEditorSettings.GetLocales();
            var locales = LocalizationLocales.DistinctIndexes(all.Select(l => l.Identifier.Code).ToList()).Select(i => all[i]).ToList();
            if (locales.Count == 0)
                return LocalizationSyncResult.Fail(
                    "This project has no Locales yet. Add at least one under Project Settings > Localization > Locale Generator, then try again.");

            // The text is the achievement's own (English) wording, so only English gets it. The other locales' cells stay
            // empty: pasting English into them would hide that they still need translating, and leaving the entry out
            // (rather than adding an empty one) lets Unity's fallback locale keep working for them.
            var source = locales[LocalizationLocales.PickSource(locales.Select(l => l.Identifier.Code).ToList())];
            var result = new LocalizationSyncResult { Ok = true, Locales = locales.Count, SourceLocale = source.Identifier.Code };
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
                        collection = LocalizationEditorSettings.CreateStringTableCollection(group.Key, root + "/" + group.Key, locales);
                        result.TablesCreated++;
                    }

                    var sourceTable = collection.GetTable(source.Identifier) as StringTable
                                      ?? collection.AddNewTable(source.Identifier) as StringTable;
                    if (sourceTable == null)
                        return LocalizationSyncResult.Fail("Could not create the '" + source.Identifier.Code + "' table of " + group.Key + ".");

                    foreach (var entry in group)
                    {
                        // A key counts as "kept" if the collection already knows it, whatever its text is:
                        // existing text belongs to whoever translated it and is never overwritten.
                        bool existed = collection.SharedData.GetEntry(entry.Key) != null;
                        if (existed) result.EntriesKept++; else result.EntriesAdded++;

                        if (sourceTable.GetEntry(entry.Key) != null) continue;
                        sourceTable.AddEntry(entry.Key, entry.Text);
                    }

                    EditorUtility.SetDirty(sourceTable);
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
