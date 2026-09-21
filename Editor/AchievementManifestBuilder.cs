using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Builds the manifest exactly as <c>scripts/export-achievement-catalog.mjs</c> does, so both tools
    /// produce a manifest <see cref="AchievementCatalog.FromJson"/> reads identically.
    /// </summary>
    internal static class AchievementManifestBuilder
    {
        /// <param name="iconStyle">games.icon_style; only "layered" adds manifest fields (a Combined manifest stays as it always was).</param>
        /// <param name="iconBackground">games.icon_background, a Resources-relative path.</param>
        /// <param name="iconInset">games.icon_inset; omitted when null or the runtime default.</param>
        public static JObject Build(long gameId, string slug, int catalogVersion, JArray rows,
            string iconStyle = null, string iconBackground = null, double? iconInset = null)
        {
            var achievements = new JArray();
            var seenBits = new System.Collections.Generic.HashSet<int>();

            foreach (var row in rows.OrderBy(r => r.Value<int>("bit_index")))
            {
                int bitIndex = row.Value<int>("bit_index");
                if (!seenBits.Add(bitIndex)) throw new InvalidOperationException("Duplicate bit index " + bitIndex);

                var entry = new JObject
                {
                    ["id"] = row.Value<long>("id"),
                    ["key"] = row.Value<string>("achievement_key"),
                    ["bitIndex"] = bitIndex,
                    ["title"] = row.Value<string>("title"),
                    ["description"] = row.Value<string>("description"),
                };

                string iconPath = row.Value<string>("icon_path");
                if (!string.IsNullOrEmpty(iconPath))
                {
                    // A full URL (http/https/www) is downloaded at runtime instead of loaded from packaged
                    // Resources, so its extension must be kept; only a local Resources-relative path has
                    // its extension stripped (Resources.Load takes no extension).
                    entry["icon"] = IsRemoteUrl(iconPath) ? iconPath : StripExtension(iconPath);
                }
                string iconUrl = row.Value<string>("icon_url");
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    // icon_url is always a remote URL — keep verbatim, no extension stripping.
                    entry["iconUrl"] = iconUrl;
                }
                if (row.Value<bool?>("hidden") == true) entry["hidden"] = true;
                if (row.Value<bool?>("is_retired") == true) entry["retired"] = true;
                int displayOrder = row.Value<int?>("display_order") ?? 0;
                if (displayOrder != 0) entry["displayOrder"] = displayOrder;

                string table = row.Value<string>("localization_table");
                if (!string.IsNullOrEmpty(table))
                {
                    var localization = new JObject { ["table"] = table };
                    string titleKey = row.Value<string>("title_key");
                    string descriptionKey = row.Value<string>("description_key");
                    if (!string.IsNullOrEmpty(titleKey)) localization["titleKey"] = titleKey;
                    if (!string.IsNullOrEmpty(descriptionKey)) localization["descriptionKey"] = descriptionKey;
                    entry["localization"] = localization;
                }

                achievements.Add(entry);
            }

            var manifest = new JObject
            {
                ["formatVersion"] = 1,
                ["game"] = slug,
                ["gameId"] = gameId,
                ["catalogVersion"] = catalogVersion,
            };

            if (string.Equals(iconStyle, "layered", StringComparison.OrdinalIgnoreCase))
            {
                manifest["iconStyle"] = "layered";
                if (!string.IsNullOrEmpty(iconBackground)) manifest["iconBackground"] = StripExtension(iconBackground);
                if (iconInset.HasValue)
                {
                    double inset = Math.Min(0.45, Math.Max(0, iconInset.Value));
                    if (Math.Abs(inset - AchievementCatalog.DefaultIconInset) > 0.0005) manifest["iconInset"] = Math.Round(inset, 3);
                }
            }

            manifest["achievements"] = achievements;
            return manifest;
        }

        private static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return dot > slash ? path.Substring(0, dot) : path;
        }

        private static bool IsRemoteUrl(string path) =>
            path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds a <c>PatreonConfig</c> asset by reflection (never a hard reference — most consumers of this
    /// package will not have the Unity Patreon Authenticator plugin installed) and reads its Supabase
    /// project URL/publishable key, the same way <c>YourGameNameAchievementsBootstrap</c> does at runtime.
    /// </summary>
    internal static class PatreonConfigReflection
    {
        private const string ResourcePath = "PatreonConfig";
        private const string MiddlewareSuffix = "/functions/v1/patreon-middleware";

        public static bool TryGetSupabaseCredentials(out string supabaseUrl, out string supabasePublishableKey)
        {
            supabaseUrl = null;
            supabasePublishableKey = null;

            Type configType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                configType = types.FirstOrDefault(t => t.Name == "PatreonConfig" && typeof(ScriptableObject).IsAssignableFrom(t));
                if (configType != null) break;
            }
            if (configType == null) return false;

            var asset = Resources.Load(ResourcePath, configType);
            if (asset == null) return false;

            string middleware = configType.GetProperty("MiddlewareApiBaseUrl")?.GetValue(asset) as string;
            supabasePublishableKey = configType.GetProperty("SupabasePublishableKey")?.GetValue(asset) as string;

            if (!string.IsNullOrEmpty(middleware) && middleware.EndsWith(MiddlewareSuffix, StringComparison.Ordinal))
                supabaseUrl = middleware.Substring(0, middleware.Length - MiddlewareSuffix.Length);

            return !string.IsNullOrEmpty(supabaseUrl) && !string.IsNullOrEmpty(supabasePublishableKey);
        }
    }
}
