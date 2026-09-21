using DryreLHub.SupabaseGameAchievements.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementCatalogImporterTests
    {
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

        [Test]
        public void BuildImportSql_keeps_hidden_and_retired_instead_of_resetting_them()
        {
            var input = new JArray
            {
                new JObject { ["id"] = 1, ["key"] = "plain", ["bitIndex"] = 0, ["title"] = "P", ["description"] = "d" },
                new JObject { ["id"] = 2, ["key"] = "secret", ["bitIndex"] = 1, ["title"] = "S", ["description"] = "d", ["hidden"] = true },
                new JObject { ["id"] = 3, ["key"] = "old", ["bitIndex"] = 2, ["title"] = "O", ["description"] = "d", ["retired"] = true },
            };

            string sql = AchievementCatalogImporter.BuildImportSql(1, "g", "G", 2, input);

            StringAssert.Contains("NULL, 0, NULL, NULL, NULL, false, false)", sql);
            StringAssert.Contains("NULL, 0, NULL, NULL, NULL, true, false)", sql);
            StringAssert.Contains("NULL, 0, NULL, NULL, NULL, false, true)", sql);
        }
    }
}
