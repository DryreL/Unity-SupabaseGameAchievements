using System.Collections.Generic;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// The one place gameplay reports events to. Every active set of rules (the dashboard's <c>rules.json</c>
    /// runner, and any <see cref="AchievementRulesBehaviour"/> you wrote) registers here, so
    /// <c>AchievementEvents.Report("boss_defeated")</c> reaches all of them and a scene trigger never has to
    /// know which one is listening.
    /// </summary>
    public static class AchievementEvents
    {
        private static readonly List<AchievementRules> Listeners = new List<AchievementRules>();
        private static bool _warnedNoRules;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Listeners.Clear();
            _warnedNoRules = false;
        }

        public static void Register(AchievementRules rules)
        {
            if (rules != null && !Listeners.Contains(rules)) Listeners.Add(rules);
        }

        public static void Unregister(AchievementRules rules) => Listeners.Remove(rules);

        /// <summary>Reports an event to every registered rule set. Unknown events are ignored.</summary>
        /// <param name="eventName">The event name a rule listens to.</param>
        /// <param name="amount">How much it counts for (1 for a click, more for "gold collected").</param>
        public static void Report(string eventName, int amount = 1)
        {
            if (Listeners.Count == 0)
            {
                if (!_warnedNoRules)
                {
                    _warnedNoRules = true;
                    Debug.LogWarning("[Achievements] Report('" + eventName + "') ignored: no achievement rules are active. " +
                        "Write rules.json from the Achievement Dashboard's Rules tab (it is loaded automatically), or add an AchievementRulesBehaviour.");
                }
                return;
            }

            // Copy: a rule may create or destroy a rules component while it runs.
            foreach (var rules in Listeners.ToArray()) rules.Report(eventName, amount);
        }
    }
}
