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
    /// <see cref="IAchievementLocalizationProvider"/> backed directly by Unity Localization.
    /// Resolves strings in the active Game Language (LocalizationSettings.SelectedLocale).
    /// </summary>
    public sealed class UnityLocalizationProvider : IAchievementLocalizationProvider, IDisposable
    {
        private readonly Dictionary<long, AchievementText> _cache = new Dictionary<long, AchievementText>();
        private readonly IAchievementLogger _logger;
        private readonly HashSet<string> _reportedFailures = new HashSet<string>(StringComparer.Ordinal);
        private int _localeGeneration;

        public UnityLocalizationProvider() : this(null) { }

        public UnityLocalizationProvider(IAchievementLogger logger)
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
                if (LocalizationSettings.InitializationOperation.IsDone)
                {
                    string title = LocalizationSettings.StringDatabase.GetLocalizedString(definition.LocalizationTable, definition.TitleKey);
                    string description = LocalizationSettings.StringDatabase.GetLocalizedString(definition.LocalizationTable, definition.DescriptionKey);

                    if (IsValid(title) || IsValid(description))
                    {
                        text = new AchievementText(
                            IsValid(title) ? title : definition.Title,
                            IsValid(description) ? description : definition.Description
                        );
                        _cache[definition.Id] = text;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                ReportOnce(definition.LocalizationTable, e);
            }

            text = AchievementText.Fallback(definition);
            return false;
        }

        public async Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken)
        {
            if (TryGetText(definition, out var ready) && IsValid(ready.Title)) return ready;

            int generation = _localeGeneration;
            try
            {
                await LocalizationSettings.InitializationOperation.Task;

                var titleOp = LocalizationSettings.StringDatabase.GetLocalizedStringAsync(definition.LocalizationTable, definition.TitleKey);
                var descOp = LocalizationSettings.StringDatabase.GetLocalizedStringAsync(definition.LocalizationTable, definition.DescriptionKey);

                await Task.WhenAll(titleOp.Task, descOp.Task);

                string title = titleOp.Result;
                string description = descOp.Result;

                var text = new AchievementText(
                    IsValid(title) ? title : definition.Title,
                    IsValid(description) ? description : definition.Description
                );

                if (generation == _localeGeneration) _cache[definition.Id] = text;
                return text;
            }
            catch (Exception e)
            {
                ReportOnce(definition.LocalizationTable, e);
                return AchievementText.Fallback(definition);
            }
        }

        private static bool IsValid(string s)
        {
            return !string.IsNullOrEmpty(s) && !s.StartsWith("No translation found for");
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
