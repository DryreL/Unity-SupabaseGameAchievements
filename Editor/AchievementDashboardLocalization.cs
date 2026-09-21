using System;
using System.Collections.Generic;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>One string table entry the dashboard wants to exist: table, key and its starting text.</summary>
    internal sealed class LocalizationEntryPlan
    {
        public LocalizationEntryPlan(string table, string key, string text)
        {
            Table = table;
            Key = key;
            Text = text;
        }

        public string Table { get; }
        public string Key { get; }
        public string Text { get; }
    }

    internal struct LocalizationSyncResult
    {
        public bool Ok;
        public string Message;
        public int TablesCreated;
        public int EntriesAdded;
        public int EntriesKept;
        public int Locales;

        public static LocalizationSyncResult Fail(string message) => new LocalizationSyncResult { Ok = false, Message = message };
    }

    /// <summary>
    /// The seam between the dashboard and Unity Localization. This assembly must build without
    /// <c>com.unity.localization</c>, so it only declares the hook; the
    /// <c>DryreLHub.SupabaseGameAchievements.Editor.Localization</c> assembly (compiled only when the package
    /// is installed) assigns <see cref="Sync"/> when the Editor loads.
    /// </summary>
    internal static class AchievementLocalizationBridge
    {
        /// <summary>
        /// Creates missing string table collections under the given asset folder (one subfolder per table)
        /// and adds the planned entries that do not exist yet. Never overwrites an existing entry.
        /// Null when Unity Localization is not installed.
        /// </summary>
        public static Func<string, IReadOnlyList<LocalizationEntryPlan>, LocalizationSyncResult> Sync;

        public static bool IsAvailable => Sync != null;
    }
}
