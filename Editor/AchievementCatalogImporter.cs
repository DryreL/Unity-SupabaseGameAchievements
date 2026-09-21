using System.Text;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Turns a manifest's achievements into an idempotent PostgreSQL script that restores them in Supabase
    /// with their original ids (used by the dashboard's "Copy/Save import SQL"; the dashboard's own Push
    /// never sends ids, so the identity sequence stays in step).
    /// </summary>
    public static class AchievementCatalogImporter
    {
        public static string BuildImportSql(long gameId, string slug, string name, int catalogVersion, JArray achievements)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- =============================================================================");
            sb.AppendLine("-- SUPABASE IMPORT SCRIPT FOR ACHIEVEMENTS");
            sb.AppendLine($"-- Game: {slug} (ID: {gameId}, Catalog Version: {catalogVersion})");
            sb.AppendLine($"-- Total achievements: {achievements.Count}");
            sb.AppendLine("-- =============================================================================\n");

            sb.AppendLine("-- 1. Ensure the game entry exists");
            sb.AppendLine("INSERT INTO public.games (id, slug, name, is_active, catalog_version)");
            sb.AppendLine($"VALUES ({gameId}, {SqlEscape(slug)}, {SqlEscape(string.IsNullOrEmpty(name) ? slug : name)}, true, {catalogVersion})");
            sb.AppendLine("ON CONFLICT (id) DO UPDATE SET");
            sb.AppendLine("  slug = EXCLUDED.slug,");
            sb.AppendLine("  name = EXCLUDED.name,");
            sb.AppendLine("  catalog_version = EXCLUDED.catalog_version;\n");

            sb.AppendLine("-- 2. Upsert all achievements (idempotent, safe against duplicate runs)");
            sb.AppendLine("INSERT INTO public.achievements (");
            sb.AppendLine("  id,");
            sb.AppendLine("  game_id,");
            sb.AppendLine("  achievement_key,");
            sb.AppendLine("  bit_index,");
            sb.AppendLine("  title,");
            sb.AppendLine("  description,");
            sb.AppendLine("  icon_path,");
            sb.AppendLine("  icon_url,");
            sb.AppendLine("  display_order,");
            sb.AppendLine("  localization_table,");
            sb.AppendLine("  title_key,");
            sb.AppendLine("  description_key,");
            sb.AppendLine("  hidden,");
            sb.AppendLine("  is_retired");
            sb.AppendLine(") VALUES");

            for (int i = 0; i < achievements.Count; i++)
            {
                var a = achievements[i];
                var loc = a["localization"] as JObject;
                long id = a.Value<long>("id");
                string key = a.Value<string>("key");
                int bitIndex = a.Value<int>("bitIndex");
                string title = a.Value<string>("title");
                string description = a.Value<string>("description");
                string icon = a.Value<string>("icon");
                string iconUrl = a.Value<string>("iconUrl");
                int displayOrder = a.Value<int?>("displayOrder") ?? 0;
                string table = loc?.Value<string>("table");
                string titleKey = loc?.Value<string>("titleKey");
                string descKey = loc?.Value<string>("descriptionKey");
                string hidden = (a.Value<bool?>("hidden") == true) ? "true" : "false";
                string retired = (a.Value<bool?>("retired") == true) ? "true" : "false";

                string comma = (i < achievements.Count - 1) ? "," : "";
                sb.AppendLine($"  ({id}, {gameId}, {SqlEscape(key)}, {bitIndex}, {SqlEscape(title)}, {SqlEscape(description)}, {SqlEscape(icon)}, {SqlEscape(iconUrl)}, {displayOrder}, {SqlEscape(table)}, {SqlEscape(titleKey)}, {SqlEscape(descKey)}, {hidden}, {retired}){comma}");
            }

            sb.AppendLine("ON CONFLICT (game_id, achievement_key) DO UPDATE SET");
            sb.AppendLine("  title = EXCLUDED.title,");
            sb.AppendLine("  description = EXCLUDED.description,");
            sb.AppendLine("  icon_path = EXCLUDED.icon_path,");
            sb.AppendLine("  icon_url = EXCLUDED.icon_url,");
            sb.AppendLine("  display_order = EXCLUDED.display_order,");
            sb.AppendLine("  localization_table = EXCLUDED.localization_table,");
            sb.AppendLine("  title_key = EXCLUDED.title_key,");
            sb.AppendLine("  description_key = EXCLUDED.description_key,");
            sb.AppendLine("  hidden = EXCLUDED.hidden,");
            sb.AppendLine("  is_retired = EXCLUDED.is_retired;\n");

            sb.AppendLine("-- 3. Sync Postgres sequence for identity column");
            sb.AppendLine("SELECT setval(pg_get_serial_sequence('public.games', 'id'), GREATEST((SELECT MAX(id) FROM public.games), 1));");
            sb.AppendLine("SELECT setval(pg_get_serial_sequence('public.achievements', 'id'), GREATEST((SELECT MAX(id) FROM public.achievements), 1));\n");

            sb.AppendLine($"-- 4. Ensure catalog_version matches achievements.json ({catalogVersion})");
            sb.AppendLine($"UPDATE public.games SET catalog_version = {catalogVersion} WHERE id = {gameId};");

            return sb.ToString();
        }

        private static string SqlEscape(string value)
        {
            if (value == null) return "NULL";
            return "'" + value.Replace("'", "''") + "'";
        }
    }
}
