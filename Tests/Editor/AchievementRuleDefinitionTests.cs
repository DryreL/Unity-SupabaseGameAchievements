using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementRuleDefinitionTests
    {
        private static AchievementRuleSet SampleSet() => new AchievementRuleSet(new[]
        {
            new AchievementRuleDefinition("r1", "opened_shop", AchievementRuleKind.Unlock),
            new AchievementRuleDefinition("r2", "clicked_link", AchievementRuleKind.Counter, 5),
            new AchievementRuleDefinition("r3", "flawless", AchievementRuleKind.Run),
        });

        // ---- json -------------------------------------------------------------------------------------------

        [Test]
        public void A_rule_set_survives_a_json_round_trip()
        {
            var parsed = AchievementRuleSet.FromJson(SampleSet().ToJson());

            Assert.AreEqual(3, parsed.Rules.Count);
            Assert.AreEqual(AchievementRuleKind.Counter, parsed.Rules[1].Kind);
            Assert.AreEqual(5, parsed.Rules[1].Target);
            Assert.AreEqual("flawless", parsed.Rules[2].AchievementKey);
        }

        [Test]
        public void The_json_is_deterministic_and_only_counters_carry_a_target()
        {
            string json = SampleSet().ToJson();

            Assert.AreEqual(json, SampleSet().ToJson());
            Assert.IsTrue(json.EndsWith("\n"));
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(json, "\"target\"").Count);
            StringAssert.Contains("\"kind\": \"run\"", json);
        }

        [Test]
        public void An_empty_rule_set_is_valid()
        {
            Assert.AreEqual(0, AchievementRuleSet.FromJson(new AchievementRuleSet(new AchievementRuleDefinition[0]).ToJson()).Rules.Count);
        }

        [Test]
        public void A_rule_of_an_unknown_kind_is_skipped_so_an_older_game_survives_a_newer_dashboard()
        {
            string json = "{ \"formatVersion\": 1, \"rules\": [" +
                "{ \"id\": \"r1\", \"achievement\": \"a\", \"kind\": \"hologram\" }," +
                "{ \"id\": \"r2\", \"achievement\": \"b\", \"kind\": \"unlock\" } ] }";

            var set = AchievementRuleSet.FromJson(json);

            Assert.AreEqual(1, set.Rules.Count);
            Assert.AreEqual("b", set.Rules[0].AchievementKey);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{ \"formatVersion\": 99, \"rules\": [] }")]
        [TestCase("{ \"formatVersion\": 1 }")]
        [TestCase("{ \"rules\": [ 5 ] }")]
        [TestCase("{ \"rules\": [ { \"id\": \"r\", \"kind\": \"unlock\" } ] }")]
        [TestCase("{ \"rules\": [ { \"id\": \"r\", \"achievement\": \"a\", \"kind\": \"counter\", \"target\": 0 } ] }")]
        [TestCase("{ \"rules\": [ { \"id\": \"r\", \"achievement\": \"a\", \"kind\": \"counter\", \"target\": \"five\" } ] }")]
        public void Structural_problems_are_reported_as_rule_exceptions(string json)
        {
            Assert.Throws<AchievementRuleException>(() => AchievementRuleSet.FromJson(json));
        }

        [Test]
        public void Duplicate_ids_and_two_counters_on_one_achievement_are_rejected()
        {
            Assert.Throws<AchievementRuleException>(() => new AchievementRuleSet(new[]
            {
                new AchievementRuleDefinition("same", "a", AchievementRuleKind.Unlock),
                new AchievementRuleDefinition("same", "b", AchievementRuleKind.Unlock),
            }));

            Assert.Throws<AchievementRuleException>(() => new AchievementRuleSet(new[]
            {
                new AchievementRuleDefinition("r1", "a", AchievementRuleKind.Counter, 3),
                new AchievementRuleDefinition("r2", "a", AchievementRuleKind.Counter, 9),
            }), "they would share and double-count one saved value");
        }

        [Test]
        public void An_unlock_rule_and_a_counter_rule_may_share_an_achievement()
        {
            Assert.DoesNotThrow(() => new AchievementRuleSet(new[]
            {
                new AchievementRuleDefinition("r1", "a", AchievementRuleKind.Unlock),
                new AchievementRuleDefinition("r2", "a", AchievementRuleKind.Counter, 3),
            }));
        }

        // ---- event names -----------------------------------------------------------------------------------

        [Test]
        public void Event_names_are_derived_from_the_rule_id_and_role()
        {
            var set = SampleSet();

            Assert.AreEqual("r1", set.Rules[0].EventName());
            Assert.AreEqual("r2", set.Rules[1].EventName("trigger"));
            Assert.AreEqual("r3:start", set.Rules[2].EventName("start"));
            Assert.AreEqual("r3:fail", set.Rules[2].EventName("fail"));
            Assert.AreEqual("r3:complete", set.Rules[2].EventName("complete"));
            CollectionAssert.AreEqual(new[] { "start", "fail", "complete" }, set.Rules[2].Roles);
            CollectionAssert.AreEqual(new[] { "trigger" }, set.Rules[0].Roles);
        }

        // ---- applying to the rules engine ------------------------------------------------------------------

        [Test]
        public void Applying_a_rule_set_makes_the_events_drive_the_achievements()
        {
            var unlocked = new HashSet<string>();
            var rules = new AchievementRules(k => unlocked.Add(k), k => unlocked.Contains(k), new InMemoryAchievementProgressStore());
            SampleSet().ApplyTo(rules);

            rules.Report("r1");
            Assert.IsTrue(unlocked.Contains("opened_shop"));

            for (int i = 0; i < 4; i++) rules.Report("r2");
            Assert.IsFalse(unlocked.Contains("clicked_link"));
            rules.Report("r2");
            Assert.IsTrue(unlocked.Contains("clicked_link"));

            rules.Report("r3:start");
            rules.Report("r3:fail");
            rules.Report("r3:complete");
            Assert.IsFalse(unlocked.Contains("flawless"), "a failed run unlocks nothing");
            rules.Report("r3:start");
            rules.Report("r3:complete");
            Assert.IsTrue(unlocked.Contains("flawless"));
        }

        [Test]
        public void An_invalid_definition_is_rejected_up_front()
        {
            Assert.Throws<ArgumentException>(() => new AchievementRuleDefinition("", "a", AchievementRuleKind.Unlock));
            Assert.Throws<ArgumentException>(() => new AchievementRuleDefinition("r", "", AchievementRuleKind.Unlock));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AchievementRuleDefinition("r", "a", AchievementRuleKind.Counter, 0));
        }
    }

    public class AchievementConditionReaderTests
    {
        private class Base
        {
            public bool BaseFlag;
        }

        private class Script : Base
        {
            public bool BossDefeated;
            private bool _hidden = true;
            public bool QuestDone() => _quest;
            public bool IsAlive => _alive;
            private bool PrivateCheck() => true;

            public int Score;
            public string Name;
            public bool WithParameter(int x) => x > 0;
            public bool this[int i] => true;

            private bool _quest;
            private bool _alive = true;

            public void SetQuest(bool value) => _quest = value;

            public bool HiddenValue => _hidden;
        }

        private static string[] Names(Type type) => AchievementConditionReader.FindMembers(type).Select(m => m.Name).ToArray();

        [Test]
        public void Finds_bool_methods_properties_and_fields_of_a_script_and_its_bases()
        {
            var names = Names(typeof(Script));

            CollectionAssert.Contains(names, "BossDefeated");
            CollectionAssert.Contains(names, "QuestDone");
            CollectionAssert.Contains(names, "IsAlive");
            CollectionAssert.Contains(names, "PrivateCheck");
            CollectionAssert.Contains(names, "BaseFlag");
        }

        [Test]
        public void Ignores_members_that_are_not_a_parameterless_bool()
        {
            var names = Names(typeof(Script));

            CollectionAssert.DoesNotContain(names, "Score");
            CollectionAssert.DoesNotContain(names, "Name");
            CollectionAssert.DoesNotContain(names, "WithParameter");
            CollectionAssert.DoesNotContain(names, "Item", "an indexer is not a condition");
            CollectionAssert.DoesNotContain(names, "SetQuest");
            Assert.IsFalse(names.Any(n => n.Contains("<")), "compiler backing fields are skipped");
        }

        [Test]
        public void Members_are_listed_once_and_sorted()
        {
            var names = Names(typeof(Script));

            CollectionAssert.AreEqual(names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
        }

        [Test]
        public void A_reader_follows_the_live_value_of_a_method_property_and_field()
        {
            var script = new Script();

            Assert.IsTrue(AchievementConditionReader.TryCreate(script, "QuestDone", out var method, out _));
            Assert.IsTrue(AchievementConditionReader.TryCreate(script, "IsAlive", out var property, out _));
            Assert.IsTrue(AchievementConditionReader.TryCreate(script, "BossDefeated", out var field, out _));
            Assert.IsTrue(AchievementConditionReader.TryCreate(script, "BaseFlag", out var baseField, out _));

            Assert.IsFalse(method());
            Assert.IsTrue(property());
            Assert.IsFalse(field());
            Assert.IsFalse(baseField());

            script.SetQuest(true);
            script.BossDefeated = true;
            script.BaseFlag = true;

            Assert.IsTrue(method());
            Assert.IsTrue(field());
            Assert.IsTrue(baseField());
        }

        [Test]
        public void A_private_member_can_be_read()
        {
            Assert.IsTrue(AchievementConditionReader.TryCreate(new Script(), "PrivateCheck", out var reader, out _));
            Assert.IsTrue(reader());
        }

        [TestCase("Missing")]
        [TestCase("Score")]
        [TestCase("WithParameter")]
        [TestCase("")]
        [TestCase(null)]
        public void A_member_that_does_not_qualify_is_reported_with_a_reason(string member)
        {
            Assert.IsFalse(AchievementConditionReader.TryCreate(new Script(), member, out var reader, out string error));

            Assert.IsNull(reader);
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void A_missing_target_is_reported()
        {
            Assert.IsFalse(AchievementConditionReader.TryCreate(null, "X", out _, out string error));
            StringAssert.Contains("target", error);
        }

        [Test]
        public void The_stripping_hint_is_part_of_the_error()
        {
            AchievementConditionReader.TryCreate(new Script(), "Missing", out _, out string error);

            StringAssert.Contains("Preserve", error);
        }

        [Test]
        public void The_edge_fires_only_when_a_condition_turns_true()
        {
            var edge = new AchievementConditionEdge();

            Assert.IsFalse(edge.Rising(false));
            Assert.IsTrue(edge.Rising(true));
            Assert.IsFalse(edge.Rising(true), "still true is not a new event");
            Assert.IsFalse(edge.Rising(false));
            Assert.IsTrue(edge.Rising(true), "false then true again is");
        }

        [Test]
        public void A_condition_that_is_already_true_on_the_first_look_fires_once()
        {
            var edge = new AchievementConditionEdge();

            Assert.IsTrue(edge.Rising(true));
            Assert.IsFalse(edge.Rising(true));

            edge.Reset();
            Assert.IsTrue(edge.Rising(true));
        }
    }
}
