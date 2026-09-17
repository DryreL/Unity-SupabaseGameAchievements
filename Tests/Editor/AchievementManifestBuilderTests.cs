using System;
using DryreLHub.SupabaseGameAchievements.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementManifestBuilderTests
    {
        private static JObject Row(long id, string key, int bitIndex, string title, string description,
            string iconPath = null, bool hidden = false, bool retired = false, int displayOrder = 0,
            string table = null, string titleKey = null, string descriptionKey = null) => new JObject
            {
                ["id"] = id,
                ["achievement_key"] = key,
                ["bit_index"] = bitIndex,
                ["title"] = title,
                ["description"] = description,
                ["icon_path"] = iconPath,
                ["hidden"] = hidden,
                ["is_retired"] = retired,
                ["display_order"] = displayOrder,
                ["localization_table"] = table,
                ["title_key"] = titleKey,
                ["description_key"] = descriptionKey,
            };

        [Test]
        public void Builds_the_manifest_envelope()
        {
            var manifest = AchievementManifestBuilder.Build(7, "YourGameName", 4, new JArray());

            Assert.AreEqual(1, manifest.Value<int>("formatVersion"));
            Assert.AreEqual("YourGameName", manifest.Value<string>("game"));
            Assert.AreEqual(7, manifest.Value<long>("gameId"));
            Assert.AreEqual(4, manifest.Value<int>("catalogVersion"));
            Assert.AreEqual(0, ((JArray)manifest["achievements"]).Count);
        }

        [Test]
        public void Sorts_by_bit_index_regardless_of_input_order()
        {
            var rows = new JArray(
                Row(2, "b", 1, "B", "d"),
                Row(1, "a", 0, "A", "d"));

            var achievements = (JArray)AchievementManifestBuilder.Build(7, "g", 1, rows)["achievements"];

            CollectionAssert.AreEqual(new[] { "a", "b" }, new[] { achievements[0].Value<string>("key"), achievements[1].Value<string>("key") });
        }

        [Test]
        public void Omits_falsy_optional_fields()
        {
            var rows = new JArray(Row(1, "a", 0, "A", "d"));
            var entry = (JObject)((JArray)AchievementManifestBuilder.Build(7, "g", 1, rows)["achievements"])[0];

            Assert.IsFalse(entry.ContainsKey("icon"));
            Assert.IsFalse(entry.ContainsKey("hidden"));
            Assert.IsFalse(entry.ContainsKey("retired"));
            Assert.IsFalse(entry.ContainsKey("displayOrder"));
            Assert.IsFalse(entry.ContainsKey("localization"));
        }

        [Test]
        public void Includes_set_optional_fields_and_strips_the_icon_extension()
        {
            var rows = new JArray(Row(1, "a", 0, "A", "d", iconPath: "YourGameName/a.webp", hidden: true, retired: true, displayOrder: 3));
            var entry = (JObject)((JArray)AchievementManifestBuilder.Build(7, "g", 1, rows)["achievements"])[0];

            Assert.AreEqual("YourGameName/a", entry.Value<string>("icon"));
            Assert.AreEqual(true, entry.Value<bool>("hidden"));
            Assert.AreEqual(true, entry.Value<bool>("retired"));
            Assert.AreEqual(3, entry.Value<int>("displayOrder"));
        }

        [Test]
        public void Builds_localization_only_when_a_table_is_set()
        {
            var rows = new JArray(Row(1, "a", 0, "A", "d", table: "ST_Achievements", titleKey: "a_title"));
            var entry = (JObject)((JArray)AchievementManifestBuilder.Build(7, "g", 1, rows)["achievements"])[0];

            var localization = (JObject)entry["localization"];
            Assert.AreEqual("ST_Achievements", localization.Value<string>("table"));
            Assert.AreEqual("a_title", localization.Value<string>("titleKey"));
            Assert.IsFalse(localization.ContainsKey("descriptionKey"), "an unset key is left out, not written as null");
        }

        [Test]
        public void Rejects_a_duplicate_bit_index()
        {
            var rows = new JArray(Row(1, "a", 0, "A", "d"), Row(2, "b", 0, "B", "d"));
            Assert.Throws<InvalidOperationException>(() => AchievementManifestBuilder.Build(7, "g", 1, rows));
        }

        [Test]
        public void Round_trips_through_AchievementCatalog_FromJson()
        {
            var rows = new JArray(
                Row(101, "first_blood", 0, "First Blood", "Defeat your first enemy.", iconPath: "g/first_blood.png",
                    table: "ST_Achievements", titleKey: "first_blood_title", descriptionKey: "first_blood_description"),
                Row(102, "retired_one", 1, "Old", "Gone.", retired: true));

            var manifest = AchievementManifestBuilder.Build(7, "g", 3, rows);
            var catalog = AchievementCatalog.FromJson(manifest.ToString());

            Assert.AreEqual(7, catalog.GameId);
            Assert.AreEqual(3, catalog.CatalogVersion);
            Assert.IsTrue(catalog.TryGetByKey("first_blood", out var def));
            Assert.AreEqual("ST_Achievements", def.LocalizationTable);
            Assert.IsTrue(catalog.TryGetByKey("retired_one", out var retired));
            Assert.IsTrue(retired.IsRetired);
        }
    }
}
