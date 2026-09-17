using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Editor tool that fetches one game's achievement catalog straight from Supabase and writes it as the
    /// manifest JSON the game ships (same format as <c>scripts/export-achievement-catalog.mjs</c> in the
    /// launcher repo, and the same shape <see cref="AchievementCatalog.FromJson"/> parses).
    /// </summary>
    /// <remarks>
    /// The Supabase URL and publishable key are looked up from a <c>PatreonConfig</c> asset via reflection
    /// (never a hard reference — most consumers of this package will not have that specific plugin), falling
    /// back to whatever was last typed into this window. Nothing here ever needs a service-role key for an
    /// active (<c>is_active = true</c>) game; the Export Key field is only for pulling an unreleased catalog
    /// and is never persisted to disk.
    /// </remarks>
    public sealed class AchievementCatalogExporterWindow : EditorWindow
    {
        private const string PrefsPrefix = "DryreLHub.Achievements.Export.";
        private const string PatreonMiddlewareSuffix = "/functions/v1/patreon-middleware";

        private string _gameSlug = "";
        private string _outputPath = "";
        private string _supabaseUrl = "";
        private string _supabasePublishableKey = "";
        private string _exportKey = ""; // session-only, never saved to EditorPrefs
        private bool _autoFillAttempted;

        private UnityWebRequest _activeRequest;
        private string _status = "";
        private MessageType _statusType = MessageType.None;
        private bool _isBusy;

        [MenuItem("Tools/DryreL Hub/Supabase Game Achievements/Export Achievement Catalog")]
        private static void Open()
        {
            var window = GetWindow<AchievementCatalogExporterWindow>(true, "Export Achievement Catalog");
            window.minSize = new Vector2(460, 300);
        }

        private void OnEnable()
        {
            _gameSlug = EditorPrefs.GetString(PrefsPrefix + "Slug", "");
            _outputPath = EditorPrefs.GetString(PrefsPrefix + "OutputPath", "");
            _supabaseUrl = EditorPrefs.GetString(PrefsPrefix + "SupabaseUrl", "");
            _supabasePublishableKey = EditorPrefs.GetString(PrefsPrefix + "PublishableKey", "");
        }

        private void OnDisable()
        {
            _activeRequest?.Dispose();
            _activeRequest = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Backend", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_isBusy))
            {
                EditorGUI.BeginChangeCheck();
                _supabaseUrl = EditorGUILayout.TextField(new GUIContent("Supabase URL", "e.g. https://xxxx.supabase.co"), _supabaseUrl);
                _supabasePublishableKey = EditorGUILayout.PasswordField(
                    new GUIContent("Publishable Key", "sb_publishable_... - the public key, safe to store locally."), _supabasePublishableKey);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                    EditorPrefs.SetString(PrefsPrefix + "PublishableKey", _supabasePublishableKey);
                }

                if (!_autoFillAttempted && (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabasePublishableKey)))
                {
                    _autoFillAttempted = true;
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out var key))
                    {
                        if (string.IsNullOrEmpty(_supabaseUrl)) _supabaseUrl = url;
                        if (string.IsNullOrEmpty(_supabasePublishableKey)) _supabasePublishableKey = key;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                        EditorPrefs.SetString(PrefsPrefix + "PublishableKey", _supabasePublishableKey);
                    }
                }

                if (GUILayout.Button("Fill from PatreonConfig", GUILayout.Width(180)))
                {
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out var key))
                    {
                        _supabaseUrl = url;
                        _supabasePublishableKey = key;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                        EditorPrefs.SetString(PrefsPrefix + "PublishableKey", _supabasePublishableKey);
                        SetStatus("Filled from Resources/PatreonConfig.asset.", MessageType.Info);
                    }
                    else
                    {
                        SetStatus("No PatreonConfig asset found (or it has no Supabase URL/key set). Enter these manually.", MessageType.Warning);
                    }
                }

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Catalog", EditorStyles.boldLabel);
                _gameSlug = EditorGUILayout.TextField(new GUIContent("Game Slug", "The games.slug row in Supabase, e.g. \"YourGameName\"."), _gameSlug);

                EditorGUILayout.BeginHorizontal();
                _outputPath = EditorGUILayout.TextField(new GUIContent("Output Path", "Where the manifest JSON is written."), _outputPath);
                if (GUILayout.Button("Browse...", GUILayout.Width(70)))
                {
                    // Matches PatreonAchievementsBootstrap's default Manifest Resource Path
                    // ("Achievements/achievements") so the zero-config path works with no extra typing.
                    string defaultName = "achievements";
                    string defaultDir = string.IsNullOrEmpty(_outputPath)
                        ? Path.Combine(Application.dataPath, "Resources", "Achievements")
                        : Path.GetDirectoryName(Path.GetFullPath(_outputPath));
                    string chosen = EditorUtility.SaveFilePanel("Export Achievement Catalog", defaultDir, defaultName, "json");
                    if (!string.IsNullOrEmpty(chosen)) _outputPath = ToProjectRelativePathIfPossible(chosen);
                }
                EditorGUILayout.EndHorizontal();

                if (string.IsNullOrEmpty(_outputPath))
                    EditorGUILayout.HelpBox("Suggested: Assets/Resources/Achievements/achievements.json " +
                        "(matches PatreonAchievementsBootstrap's default Manifest Resource Path, so the game " +
                        "loads it automatically even if nothing is assigned in the Inspector).", MessageType.None);

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Unreleased games only", EditorStyles.boldLabel);
                _exportKey = EditorGUILayout.PasswordField(
                    new GUIContent("Export Key (optional)", "A Supabase secret/service key, used only for this export if the game is is_active = false. " +
                        "Never saved to disk - re-enter it each time. Leave empty for a released game."), _exportKey);
            }

            EditorGUILayout.Space(12);

            bool canExport = !_isBusy && !string.IsNullOrEmpty(_gameSlug) && !string.IsNullOrEmpty(_outputPath)
                && !string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_supabasePublishableKey);

            using (new EditorGUI.DisabledScope(!canExport))
            {
                if (GUILayout.Button(_isBusy ? "Exporting..." : "Export", GUILayout.Height(28)))
                {
                    EditorPrefs.SetString(PrefsPrefix + "Slug", _gameSlug);
                    EditorPrefs.SetString(PrefsPrefix + "OutputPath", _outputPath);
                    StartExport();
                }
            }

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }

        private static string ToProjectRelativePathIfPossible(string absolutePath)
        {
            string full = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            string projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length);
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) ? full.Substring(projectRoot.Length) : full;
        }

        // ------------------------------------------------------------------
        // Export flow: games lookup -> achievements lookup -> write file.
        // Both requests run through UnityWebRequestAsyncOperation.completed so the Editor UI never blocks.
        // ------------------------------------------------------------------

        private void StartExport()
        {
            _isBusy = true;
            SetStatus("Fetching game '" + _gameSlug + "'...", MessageType.Info);

            string key = string.IsNullOrEmpty(_exportKey) ? _supabasePublishableKey : _exportKey;
            string url = _supabaseUrl.TrimEnd('/') + "/rest/v1/games?slug=eq." + UnityWebRequest.EscapeURL(_gameSlug) + "&select=id,slug,catalog_version";
            Send(url, key, OnGamesResponse);
        }

        private void Send(string url, string key, Action<UnityWebRequest> onDone)
        {
            _activeRequest?.Dispose();
            var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("apikey", key);
            if (key.StartsWith("eyJ", StringComparison.Ordinal)) request.SetRequestHeader("Authorization", "Bearer " + key); // JWT-style key only
            request.SetRequestHeader("Accept", "application/json");
            _activeRequest = request;
            request.SendWebRequest().completed += _ => onDone(request);
        }

        private void OnGamesResponse(UnityWebRequest request)
        {
            if (!TryReadSuccess(request, out string body)) return;

            JArray games;
            try
            {
                games = JArray.Parse(body);
            }
            catch (JsonException e)
            {
                Fail("Unexpected response for the game lookup: " + e.Message);
                return;
            }

            if (games.Count != 1)
            {
                Fail(games.Count == 0
                    ? "Game '" + _gameSlug + "' was not found. If it is unreleased (is_active = false), set the Export Key."
                    : "More than one game matched slug '" + _gameSlug + "' - this should not happen.");
                return;
            }

            long gameId = games[0].Value<long>("id");
            int catalogVersion = games[0].Value<int>("catalog_version");
            string slug = games[0].Value<string>("slug");

            SetStatus("Fetching achievements...", MessageType.Info);
            string key = string.IsNullOrEmpty(_exportKey) ? _supabasePublishableKey : _exportKey;
            string url = _supabaseUrl.TrimEnd('/') + "/rest/v1/achievements?game_id=eq." + gameId +
                "&select=id,achievement_key,bit_index,title,description,icon_path,hidden,is_retired,display_order,localization_table,title_key,description_key" +
                "&order=bit_index.asc";
            Send(url, key, request2 => OnAchievementsResponse(request2, gameId, slug, catalogVersion));
        }

        private void OnAchievementsResponse(UnityWebRequest request, long gameId, string slug, int catalogVersion)
        {
            if (!TryReadSuccess(request, out string body)) return;

            JArray rows;
            try
            {
                rows = JArray.Parse(body);
            }
            catch (JsonException e)
            {
                Fail("Unexpected response for the achievements lookup: " + e.Message);
                return;
            }

            try
            {
                var manifest = AchievementManifestBuilder.Build(gameId, slug, catalogVersion, rows);
                string json = JsonConvert.SerializeObject(manifest, Formatting.Indented) + "\n";

                string fullPath = Path.IsPathRooted(_outputPath) ? _outputPath : Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, _outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, json, new UTF8Encoding(false));

                string assetsRelative = ToProjectRelativePathIfPossible(fullPath);
                if (assetsRelative.StartsWith("Assets/", StringComparison.Ordinal)) AssetDatabase.ImportAsset(assetsRelative);

                _isBusy = false;
                SetStatus("Exported " + rows.Count + " achievement(s) for '" + slug + "' (catalog v" + catalogVersion + ") to " + _outputPath, MessageType.Info);
            }
            catch (Exception e)
            {
                Fail("Failed to write the manifest: " + e.Message);
            }
        }

        private bool TryReadSuccess(UnityWebRequest request, out string body)
        {
            body = null;
            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail("Request to " + request.url + " failed: " + request.error + (request.downloadHandler != null ? "\n" + Truncate(request.downloadHandler.text) : ""));
                return false;
            }
            body = request.downloadHandler.text;
            return true;
        }

        private void Fail(string message)
        {
            _isBusy = false;
            SetStatus(message, MessageType.Error);
        }

        private static string Truncate(string text) => text != null && text.Length > 500 ? text.Substring(0, 500) + "..." : text;
    }

    /// <summary>
    /// Builds the manifest exactly as <c>scripts/export-achievement-catalog.mjs</c> does, so both tools
    /// produce a manifest <see cref="AchievementCatalog.FromJson"/> reads identically.
    /// </summary>
    internal static class AchievementManifestBuilder
    {
        public static JObject Build(long gameId, string slug, int catalogVersion, JArray rows)
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
                if (!string.IsNullOrEmpty(iconPath)) entry["icon"] = StripExtension(iconPath);
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

            return new JObject
            {
                ["formatVersion"] = 1,
                ["game"] = slug,
                ["gameId"] = gameId,
                ["catalogVersion"] = catalogVersion,
                ["achievements"] = achievements,
            };
        }

        private static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return dot > slash ? path.Substring(0, dot) : path;
        }
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
