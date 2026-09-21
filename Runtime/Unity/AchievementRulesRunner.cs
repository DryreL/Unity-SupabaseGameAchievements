using System;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Runs the rules the Achievement Dashboard authored (<c>Resources/Achievements/rules.json</c>).
    /// <see cref="UnityAchievementManager"/> adds it automatically when that file exists, so there is nothing to
    /// put in a scene. Counters are saved with <see cref="PlayerPrefsAchievementProgressStore"/>.
    /// </summary>
    public sealed class AchievementRulesRunner : MonoBehaviour
    {
        private IAchievementProgressStore _store;

        public AchievementRules Rules { get; private set; }

        /// <summary>Parses <paramref name="rulesJson"/> and starts listening. A bad file is logged; the game runs on without rules.</summary>
        public void Initialize(string rulesJson, IAchievementLogger logger = null)
        {
            logger = logger ?? new UnityAchievementLogger(false);
            try
            {
                var set = AchievementRuleSet.FromJson(rulesJson);
                _store = new PlayerPrefsAchievementProgressStore();
                Rules = AchievementRules.ForManager(_store, logger);
                set.ApplyTo(Rules);
                AchievementEvents.Register(Rules);
                logger.Info("Loaded " + set.Rules.Count + " achievement rule(s).");
            }
            catch (Exception e)
            {
                Rules = null;
                logger.Error("Achievement rules were not loaded; achievements without rules still work. " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (Rules != null) AchievementEvents.Unregister(Rules);
            FlushProgress();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) FlushProgress();
        }

        private void OnApplicationQuit() => FlushProgress();

        private void FlushProgress() => (_store as IFlushableAchievementProgressStore)?.Flush();
    }
}
