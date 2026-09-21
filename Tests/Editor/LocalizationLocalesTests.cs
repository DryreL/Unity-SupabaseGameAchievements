using DryreLHub.SupabaseGameAchievements.Editor;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class LocalizationLocalesTests
    {
        [Test]
        public void A_locale_listed_twice_gets_one_table_only()
        {
            // Kiva's Addressables group lists every locale twice, which used to create "ST_Achievements_en 1" and friends.
            var indexes = LocalizationLocales.DistinctIndexes(new[] { "fr", "tr", "es", "ru", "en", "fr", "ru", "en", "es", "tr" });

            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, indexes);
        }

        [Test]
        public void Locale_codes_are_compared_without_regard_to_case()
        {
            CollectionAssert.AreEqual(new[] { 0 }, LocalizationLocales.DistinctIndexes(new[] { "en", "EN", "en" }));
            CollectionAssert.IsEmpty(LocalizationLocales.DistinctIndexes(new string[0]));
        }

        [Test]
        public void The_project_locale_receives_the_text()
        {
            Assert.AreEqual(1, LocalizationLocales.PickSource(new[] { "en", "tr", "fr" }, "tr"));
            Assert.AreEqual(2, LocalizationLocales.PickSource(new[] { "en", "tr", "fr" }, "FR"));
        }

        [Test]
        public void English_is_the_fallback_then_the_first_locale()
        {
            Assert.AreEqual(2, LocalizationLocales.PickSource(new[] { "tr", "fr", "en" }, null), "no project locale: English");
            Assert.AreEqual(2, LocalizationLocales.PickSource(new[] { "tr", "fr", "en" }, "de"), "project locale is not in the list: English");
            Assert.AreEqual(1, LocalizationLocales.PickSource(new[] { "tr", "en-GB" }, ""), "a regional English");
            Assert.AreEqual(0, LocalizationLocales.PickSource(new[] { "tr", "fr" }, null), "no English at all: the first one");
            Assert.AreEqual(-1, LocalizationLocales.PickSource(new string[0], "en"));
        }
    }
}
