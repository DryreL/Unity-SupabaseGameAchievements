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

        /// <summary>The locale that received the text; the others got the key only, for translators to fill in.</summary>
        public string SourceLocale;

        public static LocalizationSyncResult Fail(string message) => new LocalizationSyncResult { Ok = false, Message = message };
    }

    /// <summary>Which locales the Localize button creates tables for, and which one receives the text. Pure, so it is unit-tested.</summary>
    internal static class LocalizationLocales
    {
        /// <summary>
        /// Positions of the first occurrence of every locale code. A project can list the same locale twice (for example
        /// a duplicated Addressables entry), and each duplicate would otherwise get its own "_en 1" table asset.
        /// </summary>
        public static List<int> DistinctIndexes(IReadOnlyList<string> codes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();
            for (int i = 0; i < codes.Count; i++)
                if (seen.Add(codes[i] ?? string.Empty)) indexes.Add(i);
            return indexes;
        }

        /// <summary>
        /// The locale the achievement text is written in: English (<c>en</c>, else a regional English such as <c>en-GB</c>),
        /// else the first one. -1 when there are none.
        /// </summary>
        public static int PickSource(IReadOnlyList<string> codes)
        {
            if (codes.Count == 0) return -1;

            int Find(Func<string, bool> match)
            {
                for (int i = 0; i < codes.Count; i++)
                    if (codes[i] != null && match(codes[i])) return i;
                return -1;
            }

            int found = Find(code => string.Equals(code, "en", StringComparison.OrdinalIgnoreCase));
            if (found < 0) found = Find(code => code.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
            return found < 0 ? 0 : found;
        }
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
