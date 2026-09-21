using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementRulesTests
    {
        private sealed class RecordingLogger : IAchievementLogger
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public void Info(string message) { }

            public void Warning(string message) => Warnings.Add(message);

            public void Error(string message) => Errors.Add(message);
        }

        private HashSet<string> _unlocked;
        private List<string> _unlockCalls;
        private InMemoryAchievementProgressStore _store;
        private RecordingLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _unlocked = new HashSet<string>();
            _unlockCalls = new List<string>();
            _store = new InMemoryAchievementProgressStore();
            _logger = new RecordingLogger();
        }

        private AchievementRules Rules(IAchievementProgressStore store = null) => new AchievementRules(
            unlock: key =>
            {
                _unlockCalls.Add(key);
                return _unlocked.Add(key); // true only the first time, like AchievementManager.TryUnlock
            },
            isUnlocked: key => _unlocked.Contains(key),
            progress: store ?? _store,
            logger: _logger);

        // ---- UnlockOn --------------------------------------------------------------------------------------

        [Test]
        public void UnlockOn_unlocks_when_the_event_is_reported_and_only_then()
        {
            var rules = Rules().UnlockOn("opened_shop", "shop_opened");

            rules.Report("something_else");
            Assert.IsEmpty(_unlockCalls);

            rules.Report("shop_opened");
            CollectionAssert.AreEqual(new[] { "opened_shop" }, _unlockCalls);
        }

        [Test]
        public void An_unknown_or_empty_event_is_ignored()
        {
            var rules = Rules().UnlockOn("a", "e");

            Assert.DoesNotThrow(() => { rules.Report("nope"); rules.Report(""); rules.Report(null); });
            Assert.IsEmpty(_unlockCalls);
        }

        // ---- Counter ---------------------------------------------------------------------------------------

        [Test]
        public void A_counter_unlocks_exactly_when_it_reaches_the_target()
        {
            var rules = Rules().Counter("clicks", 5, onEvent: "click");

            for (int i = 0; i < 4; i++) rules.Report("click");
            Assert.IsEmpty(_unlockCalls, "four of five is not enough");
            Assert.AreEqual("4/5", rules.GetProgress("clicks").ToString());

            rules.Report("click");
            CollectionAssert.AreEqual(new[] { "clicks" }, _unlockCalls);
            Assert.IsTrue(rules.GetProgress("clicks").IsComplete);
        }

        [Test]
        public void A_counter_stops_once_the_achievement_is_unlocked()
        {
            var rules = Rules().Counter("clicks", 2, onEvent: "click");

            for (int i = 0; i < 10; i++) rules.Report("click");

            Assert.AreEqual(1, _unlockCalls.Count, "unlock is requested once, not on every click after");
            Assert.AreEqual(2, _store.Get("achievement.progress.clicks"), "the stored value does not run past the target");
        }

        [Test]
        public void A_report_can_count_for_more_than_one()
        {
            var rules = Rules().Counter("gold", 1000, onEvent: "gold_collected");

            rules.Report("gold_collected", 250);
            rules.Report("gold_collected", 250);
            Assert.AreEqual(500, rules.GetProgress("gold").Current);

            rules.Report("gold_collected", 900);
            Assert.AreEqual(1000, rules.GetProgress("gold").Current, "clamped at the target");
            CollectionAssert.AreEqual(new[] { "gold" }, _unlockCalls);
        }

        [Test]
        public void Progress_survives_a_restart_through_the_store()
        {
            var first = Rules().Counter("clicks", 5, onEvent: "click");
            first.Report("click");
            first.Report("click");

            var afterRestart = Rules().Counter("clicks", 5, onEvent: "click"); // same store, a new session
            Assert.AreEqual(2, afterRestart.GetProgress("clicks").Current);

            for (int i = 0; i < 3; i++) afterRestart.Report("click");
            CollectionAssert.AreEqual(new[] { "clicks" }, _unlockCalls);
        }

        [Test]
        public void Amounts_below_one_never_move_a_counter_backwards()
        {
            var rules = Rules().Counter("clicks", 5, onEvent: "click");
            rules.Report("click", 3);

            rules.Report("click", 0);
            rules.Report("click", -2);
            rules.Add("clicks", -5);

            Assert.AreEqual(3, rules.GetProgress("clicks").Current);
        }

        [Test]
        public void A_huge_amount_cannot_overflow_the_counter()
        {
            var rules = Rules().Counter("big", 10, onEvent: "e");
            rules.Report("e", 5);

            rules.Report("e", int.MaxValue);

            Assert.AreEqual(10, rules.GetProgress("big").Current);
            CollectionAssert.AreEqual(new[] { "big" }, _unlockCalls);
        }

        [Test]
        public void Add_drives_a_counter_without_an_event()
        {
            var rules = Rules().Counter("manual", 3);

            Assert.AreEqual(1, rules.Add("manual"));
            Assert.AreEqual(3, rules.Add("manual", 2));
            CollectionAssert.AreEqual(new[] { "manual" }, _unlockCalls);
        }

        [Test]
        public void Adding_to_an_unregistered_counter_warns_and_does_nothing()
        {
            var rules = Rules();

            Assert.AreEqual(0, rules.Add("typo_key"));
            Assert.AreEqual(1, _logger.Warnings.Count);
            Assert.IsEmpty(_unlockCalls);
        }

        [Test]
        public void An_achievement_unlocked_elsewhere_reports_as_complete_and_is_not_unlocked_again()
        {
            _unlocked.Add("clicks"); // e.g. restored from the server on a new device
            var rules = Rules().Counter("clicks", 5, onEvent: "click");

            Assert.IsTrue(rules.GetProgress("clicks").IsComplete);
            rules.Report("click");
            Assert.IsEmpty(_unlockCalls);
        }

        [Test]
        public void ResetProgress_starts_a_counter_over()
        {
            var rules = Rules().Counter("clicks", 5, onEvent: "click");
            rules.Report("click", 4);

            rules.ResetProgress("clicks");

            Assert.AreEqual(0, rules.GetProgress("clicks").Current);
        }

        [Test]
        public void Progress_fraction_is_zero_to_one_for_progress_bars()
        {
            var rules = Rules().Counter("clicks", 4, onEvent: "click");
            rules.Report("click");

            Assert.AreEqual(0.25f, rules.GetProgress("clicks").Fraction, 0.0001f);
            Assert.AreEqual(0f, rules.GetProgress("not_registered").Fraction);
        }

        [Test]
        public void Registering_a_bad_counter_is_reported_immediately()
        {
            var rules = Rules();

            Assert.Throws<ArgumentOutOfRangeException>(() => rules.Counter("x", 0));
            Assert.Throws<ArgumentException>(() => rules.Counter("", 5));
            rules.Counter("dup", 5);
            Assert.Throws<InvalidOperationException>(() => rules.Counter("dup", 7), "two targets for one achievement is a mistake");
        }

        [Test]
        public void Two_stores_do_not_share_progress()
        {
            var a = Rules(new InMemoryAchievementProgressStore()).Counter("clicks", 5, onEvent: "click");
            var b = Rules(new InMemoryAchievementProgressStore()).Counter("clicks", 5, onEvent: "click");

            a.Report("click", 3);

            Assert.AreEqual(3, a.GetProgress("clicks").Current);
            Assert.AreEqual(0, b.GetProgress("clicks").Current);
        }

        [Test]
        public void A_delegate_store_lets_the_game_keep_progress_in_its_own_save()
        {
            var save = new Dictionary<string, int>();
            var store = new DelegateAchievementProgressStore(k => save.TryGetValue(k, out int v) ? v : 0, (k, v) => save[k] = v);

            Rules(store).Counter("clicks", 5, onEvent: "click").Report("click", 2);

            Assert.AreEqual(2, save["achievement.progress.clicks"]);
        }

        // ---- Run (do it without failing) -------------------------------------------------------------------

        private AchievementRules FlawlessRun() =>
            Rules().Run("flawless", startEvent: "quest_started", failEvent: "player_died", completeEvent: "quest_done");

        [Test]
        public void A_run_unlocks_when_it_completes_without_failing()
        {
            var rules = FlawlessRun();

            rules.Report("quest_started");
            rules.Report("quest_done");

            CollectionAssert.AreEqual(new[] { "flawless" }, _unlockCalls);
        }

        [Test]
        public void A_failed_run_unlocks_nothing_and_the_next_attempt_starts_clean()
        {
            var rules = FlawlessRun();

            rules.Report("quest_started");
            rules.Report("player_died");
            rules.Report("quest_done");
            Assert.IsEmpty(_unlockCalls, "the attempt was spoiled");

            rules.Report("quest_started");
            rules.Report("quest_done");
            CollectionAssert.AreEqual(new[] { "flawless" }, _unlockCalls, "a fresh attempt is not affected by the earlier failure");
        }

        [Test]
        public void Completing_without_starting_does_not_unlock()
        {
            var rules = FlawlessRun();

            rules.Report("quest_done");

            Assert.IsEmpty(_unlockCalls);
        }

        [Test]
        public void Dying_outside_an_attempt_does_not_spoil_the_next_one()
        {
            var rules = FlawlessRun();

            rules.Report("player_died"); // no attempt running
            rules.Report("quest_started");
            rules.Report("quest_done");

            CollectionAssert.AreEqual(new[] { "flawless" }, _unlockCalls);
        }

        [Test]
        public void Restarting_an_attempt_forgets_an_earlier_failure()
        {
            var rules = FlawlessRun();

            rules.Report("quest_started");
            rules.Report("player_died");
            rules.Report("quest_started"); // retry without finishing
            rules.Report("quest_done");

            CollectionAssert.AreEqual(new[] { "flawless" }, _unlockCalls);
        }

        [Test]
        public void A_run_without_a_start_event_means_since_the_last_completion()
        {
            var rules = Rules().Run("streak", startEvent: null, failEvent: "lost", completeEvent: "won");

            rules.Report("won");
            CollectionAssert.AreEqual(new[] { "streak" }, _unlockCalls);

            _unlockCalls.Clear();
            _unlocked.Clear();
            rules.Report("lost");
            rules.Report("won");
            Assert.IsEmpty(_unlockCalls, "the failure spoiled this attempt");

            rules.Report("won");
            CollectionAssert.AreEqual(new[] { "streak" }, _unlockCalls, "and the next one is fresh again");
        }

        // ---- On (custom conditions) ------------------------------------------------------------------------

        [Test]
        public void A_custom_rule_can_apply_its_own_condition()
        {
            var rules = Rules().On("score_submitted", e =>
            {
                if (e.Amount >= 10000) e.Unlock("high_scorer");
            });

            rules.Report("score_submitted", 9999);
            Assert.IsEmpty(_unlockCalls);

            rules.Report("score_submitted", 10000);
            CollectionAssert.AreEqual(new[] { "high_scorer" }, _unlockCalls);
        }

        [Test]
        public void A_rule_that_throws_is_logged_and_the_others_still_run()
        {
            var rules = Rules()
                .On("e", _ => throw new InvalidOperationException("boom"))
                .UnlockOn("after_the_broken_one", "e");

            Assert.DoesNotThrow(() => rules.Report("e"));

            Assert.AreEqual(1, _logger.Errors.Count);
            StringAssert.Contains("boom", _logger.Errors[0]);
            CollectionAssert.AreEqual(new[] { "after_the_broken_one" }, _unlockCalls);
        }

        [Test]
        public void One_event_can_drive_several_rules()
        {
            var rules = Rules()
                .Counter("kills", 2, onEvent: "enemy_killed")
                .UnlockOn("first_blood", "enemy_killed");

            rules.Report("enemy_killed");
            rules.Report("enemy_killed");

            CollectionAssert.AreEquivalent(new[] { "first_blood", "kills" }, _unlocked);
        }

        [Test]
        public void A_rule_may_register_more_rules_while_running()
        {
            var rules = Rules();
            rules.On("e", _ => rules.UnlockOn("late", "e"));

            Assert.DoesNotThrow(() => rules.Report("e"));
        }
    }
}
