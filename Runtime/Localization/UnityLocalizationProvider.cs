using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// <see cref="IAchievementLocalizationProvider"/> backed by the Unity Localization package. Only compiled
    /// when com.unity.localization is installed (assembly define constraint), so the core never depends on it.
    /// </summary>
    /// <remarks>
    /// Resolution per field: the achievement's <c>localization.table</c> + <c>titleKey</c>/<c>descriptionKey</c>
    /// in the currently selected locale. A missing table, missing entry, empty value, or load failure falls
    /// back to the manifest's title/description for that field only. Results are cached per locale.
    /// Call from the main thread (Unity Localization's own requirement).
    /// </remarks>
    public sealed class UnityLocalizationProvider : IAchievementLocalizationProvider, IDisposable
    {
        private readonly Dictionary<long, AchievementText> _cache = new Dictionary<long, AchievementText>();
        private readonly IAchievementLogger _logger;
        private readonly HashSet<string> _reportedFailures = new HashSet<string>(StringComparer.Ordinal);
        private int _localeGeneration;

        public UnityLocalizationProvider(IAchievementLogger logger = null)
        {
            _logger = logger ?? new UnityAchievementLogger(false);
            LocalizationSettings.SelectedLocaleChanged += OnSelectedLocaleChanged;
        }

        public void Dispose()
        {
            LocalizationSettings.SelectedLocaleChanged -= OnSelectedLocaleChanged;
        }

        public bool TryGetText(AchievementDefinition definition, out AchievementText text)
        {
            if (!definition.HasLocalization)
            {
                text = AchievementText.Fallback(definition);
                return true;
            }
            if (_cache.TryGetValue(definition.Id, out text)) return true;

            try
            {
                if (!LocalizationSettings.InitializationOperation.IsDone) return false;
                var tableOperation = LocalizationSettings.StringDatabase.GetTableAsync(definition.LocalizationTable);
                if (!tableOperation.IsDone) return false;

                var table = tableOperation.Status == AsyncOperationStatus.Succeeded ? tableOperation.Result : null;
                text = Resolve(definition, table);
                _cache[definition.Id] = text;
                return true;
            }
            catch (Exception e)
            {
                ReportOnce(definition.LocalizationTable, e);
                text = AchievementText.Fallback(definition);
                return true;
            }
        }

        public async Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken)
        {
            if (TryGetText(definition, out var ready)) return ready;

            int generation = _localeGeneration;
            try
            {
                await LocalizationSettings.InitializationOperation.Task;
                var table = await LocalizationSettings.StringDatabase.GetTableAsync(definition.LocalizationTable).Task;
                var text = Resolve(definition, table);
                if (generation == _localeGeneration) _cache[definition.Id] = text;
                return text;
            }
            catch (Exception e)
            {
                ReportOnce(definition.LocalizationTable, e);
                return AchievementText.Fallback(definition);
            }
        }

        private AchievementText Resolve(AchievementDefinition definition, StringTable table)
        {
            if (table == null) ReportOnce(definition.LocalizationTable, null);
            string title = Lookup(table, definition.TitleKey) ?? definition.Title;
            string description = Lookup(table, definition.DescriptionKey) ?? definition.Description;
            return new AchievementText(title, description);
        }

        private static string Lookup(StringTable table, string key)
        {
            if (table == null || string.IsNullOrEmpty(key)) return null;
            var entry = table.GetEntry(key);
            if (entry == null) return null;
            string value = entry.GetLocalizedString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private void OnSelectedLocaleChanged(Locale locale)
        {
            _localeGeneration++;
            _cache.Clear();
        }

        private void ReportOnce(string tableName, Exception exception)
        {
            if (!_reportedFailures.Add(tableName ?? string.Empty)) return;
            _logger.Warning("Localization table '" + tableName + "' is unavailable; using fallback achievement text." +
                            (exception != null ? " " + exception.Message : string.Empty));
        }
    }
}

