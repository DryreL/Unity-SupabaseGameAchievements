using DryreLHub.SupabaseGameAchievements.Unity;

namespace DryreLHub.SupabaseGameAchievements.Samples
{
    /// <summary>
    /// Every achievement condition of the game, in one place. Add this to an object in your first scene; the
    /// achievement system itself is initialized as usual (see ExampleAchievementBootstrap).
    ///
    /// Gameplay never mentions an achievement key. It only reports what happened:
    ///
    ///     AchievementRulesBehaviour.Report("enemy_killed");           // from code
    ///     AchievementRulesBehaviour.Report("gold_collected", 250);    // counts for 250
    ///
    /// or, with no code, a Button's OnClick -> this component -> ReportEvent(string) -> type "link_clicked".
    /// </summary>
    public sealed class ExampleAchievementRules : AchievementRulesBehaviour
    {
        protected override void Configure(AchievementRules rules)
        {
            rules
                // "Click 5 times": every "link_clicked" adds 1; the counter is saved, so it survives a restart.
                .Counter("clicked_link_5_times", target: 5, onEvent: "link_clicked")

                // "Defeat 100 enemies".
                .Counter("centurion", target: 100, onEvent: "enemy_killed")

                // A report can count for more than one: 1000 gold in total across the whole game.
                .Counter("gold_hoarder", target: 1000, onEvent: "gold_collected")

                // One event, one achievement.
                .UnlockOn("first_blood", onEvent: "enemy_killed")
                .UnlockOn("opened_shop", onEvent: "shop_opened")

                // "Finish the quest without dying": completing unlocks only if "player_died" did not happen
                // in between. Failing simply unlocks nothing; the next quest_started is a fresh attempt.
                .Run("flawless_quest", startEvent: "quest_started", failEvent: "player_died", completeEvent: "quest_done")

                // Anything the built-ins do not cover is a plain handler with your own condition.
                .On("score_submitted", e =>
                {
                    if (e.Amount >= 10000) e.Unlock("high_scorer");
                });
        }

        // Optional: show "3/5" in a menu.
        public string DescribeClickProgress() => Rules != null ? Rules.GetProgress("clicked_link_5_times").ToString() : "?";
    }
}
