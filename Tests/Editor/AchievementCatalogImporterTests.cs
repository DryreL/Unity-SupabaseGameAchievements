using DryreLHub.SupabaseGameAchievements.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementCatalogImporterTests
    {
        [Test]
        public void BuildGamePayload_constructs_correct_fields()
        {
            var payload = AchievementCatalogImporter.BuildGamePayload(1, "<YourGameName>", "<YourGameName>", 15);

            Assert.AreEqual(1, payload.Value<long>("id"));
            Assert.AreEqual("<YourGameName>", payload.Value<string>("slug"));
            Assert.AreEqual("<YourGameName>", payload.Value<string>("name"));
            Assert.AreEqual(true, payload.Value<bool>("is_active"));
            Assert.AreEqual(15, payload.Value<int>("catalog_version"));
        }

        [Test]
        public void BuildAchievementsPayload_maps_manifest_keys_to_supabase_columns()
        {
            var input = new JArray
            {
                new JObject
                {
                    ["id"] = 1,
                    ["key"] = "first_blood",
                    ["bitIndex"] = 0,
                    ["title"] = "First Blood",
                    ["description"] = "Defeat your first enemy.",
                    ["icon"] = "<YourGameName>/first_blood",
                    ["displayOrder"] = 1,
                    ["localization"] = new JObject
                    {
                        ["table"] = "ST_Achievements",
                        ["titleKey"] = "first_blood_title",
                        ["descriptionKey"] = "first_blood_description"
                    }
                }
            };

            var payload = AchievementCatalogImporter.BuildAchievementsPayload(1, input);
            Assert.AreEqual(1, payload.Count);

            var first = (JObject)payload[0];
            Assert.AreEqual(1, first.Value<long>("id"));
            Assert.AreEqual(1, first.Value<long>("game_id"));
            Assert.AreEqual("first_blood", first.Value<string>("achievement_key"));
            Assert.AreEqual(0, first.Value<int>("bit_index"));
            Assert.AreEqual("First Blood", first.Value<string>("title"));
            Assert.AreEqual("Defeat your first enemy.", first.Value<string>("description"));
            Assert.AreEqual("<YourGameName>/first_blood", first.Value<string>("icon_path"));
            Assert.AreEqual(1, first.Value<int>("display_order"));
            Assert.AreEqual("ST_Achievements", first.Value<string>("localization_table"));
            Assert.AreEqual("first_blood_title", first.Value<string>("title_key"));
            Assert.AreEqual("first_blood_description", first.Value<string>("description_key"));
            Assert.AreEqual(false, first.Value<bool>("hidden"));
            Assert.AreEqual(false, first.Value<bool>("is_retired"));
        }

        [Test]
        public void BuildImportSql_generates_idempotent_sql()
        {
            var input = new JArray
            {
                new JObject
                {
                    ["id"] = 27,
                    ["key"] = "letsgo",
                    ["bitIndex"] = 26,
                    ["title"] = "Let's Go!",
                    ["description"] = "Start the game.",
                    ["icon"] = "<YourGameName>/letsgo"
                }
            };

            string sql = AchievementCatalogImporter.BuildImportSql(1, "<YourGameName>", "<YourGameName>", 15, input);

            StringAssert.Contains("INSERT INTO public.games", sql);
            StringAssert.Contains("ON CONFLICT (id) DO UPDATE SET", sql);
            StringAssert.Contains("INSERT INTO public.achievements", sql);
            StringAssert.Contains("ON CONFLICT (game_id, achievement_key) DO UPDATE SET", sql);
            StringAssert.Contains("'Let''s Go!'", sql); // properly escaped apostrophe
            StringAssert.Contains("setval(pg_get_serial_sequence('public.achievements', 'id')", sql);
            StringAssert.Contains("UPDATE public.games SET catalog_version = 15 WHERE id = 1;", sql);
        }
    }
}
