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
    /// in the currently selected locale. If the selected language does not support the achievement or is missing,
    /// it falls back to the supported system language, and then to the manifest's default text.
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

                // If fields are still missing/empty and active language doesn't support them, fallback to supported system language
                if (string.IsNullOrEmpty(text.Title) || string.IsNullOrEmpty(text.Description))
                {
                    try
                    {
                        string code = GetSupportedLanguageCode(Application.systemLanguage);
                        var fallbackLocale = LocalizationSettings.AvailableLocales?.GetLocale(code);
                        if (fallbackLocale != null)
                        {
                            var fallbackTable = await LocalizationSettings.StringDatabase.GetTableAsync(definition.LocalizationTable, fallbackLocale).Task;
                            if (fallbackTable != null && fallbackTable != table)
                            {
                                string t = Lookup(fallbackTable, definition.TitleKey) ?? text.Title;
                                string d = Lookup(fallbackTable, definition.DescriptionKey) ?? text.Description;
                                text = new AchievementText(t ?? definition.Title, d ?? definition.Description);
                            }
                        }
                    }
                    catch { }
                }

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
            string title = Lookup(table, definition.TitleKey);
            string description = Lookup(table, definition.DescriptionKey);

            // Fallback to supported system language if current table doesn't have the entry
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(description))
            {
                var fallbackTable = ResolveSupportedSystemLocaleTable(definition.LocalizationTable);
                if (fallbackTable != null && fallbackTable != table)
                {
                    if (string.IsNullOrEmpty(title)) title = Lookup(fallbackTable, definition.TitleKey);
                    if (string.IsNullOrEmpty(description)) description = Lookup(fallbackTable, definition.DescriptionKey);
                }
            }

            title = title ?? definition.Title;
            description = description ?? definition.Description;
            return new AchievementText(title, description);
        }

        private StringTable ResolveSupportedSystemLocaleTable(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return null;
            try
            {
                string code = GetSupportedLanguageCode(Application.systemLanguage);
                var locale = LocalizationSettings.AvailableLocales?.GetLocale(code);
                if (locale != null)
                {
                    var op = LocalizationSettings.StringDatabase.GetTableAsync(tableName, locale);
                    if (op.IsDone && op.Status == AsyncOperationStatus.Succeeded)
                    {
                        return op.Result;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string GetSupportedLanguageCode(SystemLanguage lang)
        {
            switch (lang)
            {
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.English: return "en";
                default: return "en"; // Default supported system language
            }
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
