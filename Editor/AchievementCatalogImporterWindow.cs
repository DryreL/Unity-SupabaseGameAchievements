using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Core logic for transforming an achievements.json manifest into Supabase PostgREST
    /// payloads and an idempotent PostgreSQL import script.
    /// </summary>
    public static class AchievementCatalogImporter
    {
        public static JObject BuildGamePayload(long gameId, string slug, string name, int catalogVersion)
        {
            return new JObject
            {
                ["id"] = gameId,
                ["slug"] = slug,
                ["name"] = string.IsNullOrEmpty(name) ? slug : name,
                ["is_active"] = true,
                ["catalog_version"] = catalogVersion
            };
        }

        public static JArray BuildAchievementsPayload(long gameId, JArray achievements)
        {
            var result = new JArray();
            foreach (var a in achievements)
            {
                var loc = a["localization"] as JObject;
                var obj = new JObject
                {
                    ["id"] = a.Value<long>("id"),
                    ["game_id"] = gameId,
                    ["achievement_key"] = a.Value<string>("key"),
                    ["bit_index"] = a.Value<int>("bitIndex"),
                    ["title"] = a.Value<string>("title"),
                    ["description"] = a.Value<string>("description"),
                    ["icon_path"] = a.Value<string>("icon"),
                    ["display_order"] = a.Value<int?>("displayOrder") ?? 0,
                    ["localization_table"] = loc?.Value<string>("table"),
                    ["title_key"] = loc?.Value<string>("titleKey"),
                    ["description_key"] = loc?.Value<string>("descriptionKey"),
                    ["hidden"] = false,
                    ["is_retired"] = false
                };
                result.Add(obj);
            }
            return result;
        }

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
                int displayOrder = a.Value<int?>("displayOrder") ?? 0;
                string table = loc?.Value<string>("table");
                string titleKey = loc?.Value<string>("titleKey");
                string descKey = loc?.Value<string>("descriptionKey");

                string comma = (i < achievements.Count - 1) ? "," : "";
                sb.AppendLine($"  ({id}, {gameId}, {SqlEscape(key)}, {bitIndex}, {SqlEscape(title)}, {SqlEscape(description)}, {SqlEscape(icon)}, {displayOrder}, {SqlEscape(table)}, {SqlEscape(titleKey)}, {SqlEscape(descKey)}, false, false){comma}");
            }

            sb.AppendLine("ON CONFLICT (game_id, achievement_key) DO UPDATE SET");
            sb.AppendLine("  title = EXCLUDED.title,");
            sb.AppendLine("  description = EXCLUDED.description,");
            sb.AppendLine("  icon_path = EXCLUDED.icon_path,");
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

    /// <summary>
    /// Editor tool that imports an achievements.json manifest into Supabase via PostgREST
    /// (using a service_role key), or generates and copies an idempotent SQL import script.
    /// </summary>
    public sealed class AchievementCatalogImporterWindow : EditorWindow
    {
        private const string PrefsPrefix = "DryreLHub.Achievements.Import.";

        private string _jsonPath = "";
        private TextAsset _jsonAsset;
        private string _gameName = "";
        private string _supabaseUrl = "";
        private string _serviceKey = ""; // session-only, never persisted
        private bool _autoFillAttempted;

        private JObject _parsedManifest;
        private JArray _parsedAchievements;
        private long _gameId;
        private string _gameSlug = "";
        private int _catalogVersion;

        private Vector2 _scrollPos;
        private bool _showListFoldout = true;
        private string _status = "";
        private MessageType _statusType = MessageType.None;
        private bool _isBusy;

        [MenuItem("Tools/DryreL Hub/Supabase Game Achievements/Import Achievement Catalog")]
        public static void Open()
        {
            var window = GetWindow<AchievementCatalogImporterWindow>(true, "Import Achievement Catalog");
            window.minSize = new Vector2(540, 520);
        }

        private void OnEnable()
        {
            _supabaseUrl = EditorPrefs.GetString(PrefsPrefix + "SupabaseUrl", "");
            _jsonPath = EditorPrefs.GetString(PrefsPrefix + "JsonPath", "Assets/Resources/Achievements/achievements.json");
            _gameName = EditorPrefs.GetString(PrefsPrefix + "GameName", "");

            TryAutoLoadDefaultJson();
        }

        private void TryAutoLoadDefaultJson()
        {
            if (!string.IsNullOrEmpty(_jsonPath) && File.Exists(_jsonPath))
            {
                LoadJsonFile(_jsonPath);
            }
            else
            {
                string fallback = Path.Combine(Application.dataPath, "Resources", "Achievements", "achievements.json");
                if (File.Exists(fallback))
                {
                    _jsonPath = "Assets/Resources/Achievements/achievements.json";
                    LoadJsonFile(fallback);
                }
            }
        }

        private void LoadJsonFile(string path)
        {
            try
            {
                string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, path);
                if (!File.Exists(fullPath))
                {
                    Fail("File not found: " + fullPath);
                    return;
                }

                string content = File.ReadAllText(fullPath, Encoding.UTF8);
                ParseManifestJson(content);
                _jsonPath = path;
                EditorPrefs.SetString(PrefsPrefix + "JsonPath", _jsonPath);
            }
            catch (Exception ex)
            {
                Fail("Failed to read JSON: " + ex.Message);
            }
        }

        private void ParseManifestJson(string content)
        {
            try
            {
                _parsedManifest = JObject.Parse(content);
                _gameSlug = _parsedManifest.Value<string>("game") ?? "";
                _gameId = _parsedManifest.Value<long>("gameId");
                _catalogVersion = _parsedManifest.Value<int>("catalogVersion");
                _parsedAchievements = _parsedManifest["achievements"] as JArray ?? new JArray();

                if (string.IsNullOrEmpty(_gameName) && !string.IsNullOrEmpty(_gameSlug))
                {
                    _gameName = char.ToUpperInvariant(_gameSlug[0]) + _gameSlug.Substring(1);
                    EditorPrefs.SetString(PrefsPrefix + "GameName", _gameName);
                }

                SetStatus($"Loaded {_parsedAchievements.Count} achievements for '{_gameSlug}' (Game ID: {_gameId}, Catalog v{_catalogVersion}).", MessageType.Info);
            }
            catch (Exception ex)
            {
                _parsedManifest = null;
                _parsedAchievements = null;
                Fail("Invalid JSON format: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Manifest Source", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_isBusy))
            {
                EditorGUI.BeginChangeCheck();
                _jsonAsset = (TextAsset)EditorGUILayout.ObjectField("JSON Asset", _jsonAsset, typeof(TextAsset), false);
                if (EditorGUI.EndChangeCheck() && _jsonAsset != null)
                {
                    _jsonPath = AssetDatabase.GetAssetPath(_jsonAsset);
                    EditorPrefs.SetString(PrefsPrefix + "JsonPath", _jsonPath);
                    ParseManifestJson(_jsonAsset.text);
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                _jsonPath = EditorGUILayout.TextField(new GUIContent("File Path", "Path to achievements.json"), _jsonPath);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PrefsPrefix + "JsonPath", _jsonPath);
                }

                if (GUILayout.Button("Browse...", GUILayout.Width(75)))
                {
                    string selected = EditorUtility.OpenFilePanel("Select achievements.json", "Assets/Resources/Achievements", "json");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        LoadJsonFile(selected);
                    }
                }
                if (GUILayout.Button("Reload", GUILayout.Width(65)))
                {
                    LoadJsonFile(_jsonPath);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Game Metadata", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                _gameName = EditorGUILayout.TextField(new GUIContent("Display Name", "Game title shown in database (e.g. Hellasure)."), _gameName);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PrefsPrefix + "GameName", _gameName);
                }

                EditorGUILayout.LabelField($"Slug: {_gameSlug}  |  Game ID: {_gameId}  |  Catalog Version: {_catalogVersion}  |  Count: {(_parsedAchievements?.Count ?? 0)}", EditorStyles.miniLabel);

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Supabase Backend", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                _supabaseUrl = EditorGUILayout.TextField(new GUIContent("Supabase URL", "e.g. https://xxxx.supabase.co"), _supabaseUrl);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                }

                if (!_autoFillAttempted && string.IsNullOrEmpty(_supabaseUrl))
                {
                    _autoFillAttempted = true;
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out _))
                    {
                        _supabaseUrl = url;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                    }
                }

                if (GUILayout.Button("Auto-fill URL from PatreonConfig", GUILayout.Width(220)))
                {
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out _))
                    {
                        _supabaseUrl = url;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                        SetStatus("Filled Supabase URL from Resources/PatreonConfig.asset.", MessageType.Info);
                    }
                    else
                    {
                        SetStatus("No PatreonConfig asset found (or it has no Middleware URL set). Enter URL manually.", MessageType.Warning);
                    }
                }

                _serviceKey = EditorGUILayout.PasswordField(
                    new GUIContent("Service Role Key", "Supabase service_role / secret key. Required for direct REST upload. Never saved to disk - enter each session."),
                    _serviceKey);

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();

                bool canUpload = !_isBusy && _parsedAchievements != null && _parsedAchievements.Count > 0 &&
                                 !string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_serviceKey);

                using (new EditorGUI.DisabledScope(!canUpload))
                {
                    if (GUILayout.Button("Upload to Supabase (REST API)", GUILayout.Height(32)))
                    {
                        UploadToSupabase();
                    }
                }

                bool canSql = _parsedAchievements != null && _parsedAchievements.Count > 0;

                using (new EditorGUI.DisabledScope(!canSql))
                {
                    if (GUILayout.Button("Copy SQL to Clipboard", GUILayout.Height(32)))
                    {
                        string sql = AchievementCatalogImporter.BuildImportSql(_gameId, _gameSlug, _gameName, _catalogVersion, _parsedAchievements);
                        GUIUtility.systemCopyBuffer = sql;
                        SetStatus("SQL import script copied to clipboard! Paste into Supabase SQL Editor.", MessageType.Info);
                    }

                    if (GUILayout.Button("Save SQL File...", GUILayout.Height(32), GUILayout.Width(110)))
                    {
                        string defaultDir = "Assets/Resources/Achievements";
                        string path = EditorUtility.SaveFilePanel("Save Import SQL Script", defaultDir, "import_achievements.sql", "sql");
                        if (!string.IsNullOrEmpty(path))
                        {
                            string sql = AchievementCatalogImporter.BuildImportSql(_gameId, _gameSlug, _gameName, _catalogVersion, _parsedAchievements);
                            File.WriteAllText(path, sql, new UTF8Encoding(false));
                            AssetDatabase.Refresh();
                            SetStatus("SQL script saved to " + path, MessageType.Info);
                        }
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            if (_parsedAchievements != null && _parsedAchievements.Count > 0)
            {
                EditorGUILayout.Space(8);
                _showListFoldout = EditorGUILayout.Foldout(_showListFoldout, $"Parsed Achievements ({_parsedAchievements.Count})", true);
                if (_showListFoldout)
                {
                    using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos, "box", GUILayout.ExpandHeight(true)))
                    {
                        _scrollPos = scroll.scrollPosition;
                        foreach (var a in _parsedAchievements)
                        {
                            long id = a.Value<long>("id");
                            string key = a.Value<string>("key");
                            int bit = a.Value<int>("bitIndex");
                            string title = a.Value<string>("title");

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField($"#{id}", GUILayout.Width(35));
                                EditorGUILayout.LabelField($"bit {bit}", GUILayout.Width(45));
                                EditorGUILayout.LabelField(key, GUILayout.Width(170));
                                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                            }
                        }
                    }
                }
            }
        }

        private void UploadToSupabase()
        {
            _isBusy = true;
            SetStatus("1/3 Upserting game entry in Supabase...", MessageType.Info);

            var gamePayload = AchievementCatalogImporter.BuildGamePayload(_gameId, _gameSlug, _gameName, _catalogVersion);
            string gamesUrl = _supabaseUrl.TrimEnd('/') + "/rest/v1/games?on_conflict=id";

            SendRestRequest(gamesUrl, "POST", gamePayload.ToString(Formatting.None), "resolution=merge-duplicates", reqGame =>
            {
                if (!TryReadSuccess(reqGame, out _)) return;

                SetStatus("2/3 Upserting achievements in batch...", MessageType.Info);
                var achievementsPayload = AchievementCatalogImporter.BuildAchievementsPayload(_gameId, _parsedAchievements);
                string achievementsUrl = _supabaseUrl.TrimEnd('/') + "/rest/v1/achievements?on_conflict=game_id,achievement_key";

                SendRestRequest(achievementsUrl, "POST", achievementsPayload.ToString(Formatting.None), "resolution=merge-duplicates", reqAch =>
                {
                    if (!TryReadSuccess(reqAch, out _)) return;

                    SetStatus("3/3 Syncing catalog_version...", MessageType.Info);
                    string patchUrl = _supabaseUrl.TrimEnd('/') + "/rest/v1/games?id=eq." + _gameId;
                    var patchPayload = new JObject { ["catalog_version"] = _catalogVersion };

                    SendRestRequest(patchUrl, "PATCH", patchPayload.ToString(Formatting.None), null, reqPatch =>
                    {
                        if (!TryReadSuccess(reqPatch, out _)) return;

                        _isBusy = false;
                        SetStatus($"Successfully imported {_parsedAchievements.Count} achievements for '{_gameSlug}' (catalog v{_catalogVersion}) into Supabase!", MessageType.Info);
                    });
                });
            });
        }

        private void SendRestRequest(string url, string method, string body, string prefer, Action<UnityWebRequest> onDone)
        {
            var request = new UnityWebRequest(url, method)
            {
                uploadHandler = !string.IsNullOrEmpty(body) ? new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)) : null,
                downloadHandler = new DownloadHandlerBuffer()
            };

            request.SetRequestHeader("apikey", _serviceKey);
            request.SetRequestHeader("Authorization", "Bearer " + _serviceKey);
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(prefer))
            {
                request.SetRequestHeader("Prefer", prefer);
            }

            request.SendWebRequest().completed += _ => onDone(request);
        }

        private bool TryReadSuccess(UnityWebRequest request, out string body)
        {
            body = null;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string detail = request.downloadHandler?.text;
                Fail($"Request to {request.url} failed ({request.responseCode} {request.error}):\n" + (string.IsNullOrEmpty(detail) ? "" : detail));
                return false;
            }
            body = request.downloadHandler?.text;
            return true;
        }

        private void Fail(string message)
        {
            _isBusy = false;
            SetStatus(message, MessageType.Error);
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }
    }
}
