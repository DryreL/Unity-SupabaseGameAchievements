using System.Linq;
using DryreLHub.SupabaseGameAchievements.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementRuleWiringTests
    {
        // Shaped like UnityEngine.UI.Button: a private serialized field behind a public property (the same event).
        private class ButtonLike
        {
            [SerializeField] private UnityEvent m_OnClick = new UnityEvent();

            public UnityEvent onClick => m_OnClick;
        }

        private class ScriptWithEvents
        {
            public UnityEvent OnBossKilled = new UnityEvent();
            [SerializeField] private UnityEvent<int> _onScored = new UnityEvent<int>();
            private UnityEvent _notSerialized = new UnityEvent();
            public UnityEvent NotAnEventValue;
            public int Plain;

            public UnityEvent<int> Scored => _onScored;
        }

        private class Derived : ScriptWithEvents
        {
            public UnityEvent OnLevelDone = new UnityEvent();
        }

        private static string[] Names(object owner) =>
            AchievementRuleWiring.FindEventsOn(owner).Select(e => e.Name).ToArray();

        [Test]
        public void A_buttons_onClick_is_found_once_under_its_public_name()
        {
            var names = Names(new ButtonLike());

            CollectionAssert.AreEqual(new[] { "onClick" }, names, "the private m_OnClick is the same event, not a second one");
        }

        [Test]
        public void Public_and_serialized_event_fields_are_found_including_generic_events()
        {
            var names = Names(new ScriptWithEvents());

            CollectionAssert.Contains(names, "OnBossKilled");
            CollectionAssert.Contains(names, "Scored", "UnityEvent<T> can be hooked too: the trigger ignores the argument");
        }

        [Test]
        public void An_unserialized_private_event_a_null_field_and_non_events_are_not_offered()
        {
            var names = Names(new ScriptWithEvents());

            CollectionAssert.DoesNotContain(names, "_notSerialized");
            CollectionAssert.DoesNotContain(names, "NotAnEventValue");
            CollectionAssert.DoesNotContain(names, "Plain");
        }

        [Test]
        public void Events_of_base_classes_are_included()
        {
            var names = Names(new Derived());

            CollectionAssert.Contains(names, "OnLevelDone");
            CollectionAssert.Contains(names, "OnBossKilled");
        }

        [Test]
        public void The_events_are_the_live_instances_and_are_listed_in_name_order()
        {
            var owner = new ScriptWithEvents();
            var found = AchievementRuleWiring.FindEventsOn(owner);

            Assert.AreSame(owner.OnBossKilled, found.First(e => e.Name == "OnBossKilled").Event, "binding has to modify the component's own event");
            CollectionAssert.AreEqual(found.Select(e => e.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray(), found.Select(e => e.Name).ToArray());
        }

        [Test]
        public void An_object_with_no_events_yields_an_empty_list()
        {
            Assert.IsEmpty(AchievementRuleWiring.FindEventsOn(new object()));
        }
    }
}
