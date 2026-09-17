using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Retires or permanently deletes achievements straight from Supabase, without needing the SQL editor
    /// for routine catalog maintenance. Calls the <c>retire_achievement</c>/<c>delete_retired_achievement</c>
    /// RPCs added in the launcher's <c>20260918000000_achievement_admin.sql</c> migration, which are
    /// service_role-only - a client publishable key cannot call them, by design.
    /// </summary>
    /// <remarks>
    /// <b>Retire</b> is the normal, safe way to remove an achievement: it stays in the database forever
    /// (its <c>bit_index</c> is never reused) so old clients and player history stay valid.
    /// <b>Delete</b> is a hard delete for a genuine mistake that never shipped: the RPC itself refuses
    /// unless the achievement is already retired and no player has ever unlocked it, and this window
    /// additionally asks you to type the achievement key back to confirm before sending the request.
    /// </remarks>
    public sealed class AchievementManagementWindow : EditorWindow
    {
        private const string PrefsPrefix = "DryreLHub.Achievements.Manage.";

        private string _gameSlug = "";
        private string _supabaseUrl = "";
        private string _serviceKey = ""; // service_role/secret key - session-only, never persisted
        private bool _autoFillAttempted;

        private long _gameId;
        private JArray _achievements;
        private string _status = "";
        private MessageType _statusType = MessageType.None;
        private bool _isBusy;

        private long _pendingDeleteId;
        private string _pendingDeleteKey;
        private string _deleteConfirmationText = "";

        [MenuItem("Tools/DryreL Hub/Supabase Game Achievements/Manage Achievements")]
        private static void Open()
        {
            var window = GetWindow<AchievementManagementWindow>(true, "Manage Achievements");
            window.minSize = new Vector2(520, 360);
        }

        private void OnEnable()
        {
            _gameSlug = EditorPrefs.GetString(PrefsPrefix + "Slug", "");
            _supabaseUrl = EditorPrefs.GetString(PrefsPrefix + "SupabaseUrl", "");
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(_isBusy))
            {
                EditorGUI.BeginChangeCheck();
                _supabaseUrl = EditorGUILayout.TextField("Supabase URL", _supabaseUrl);
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);

                if (!_autoFillAttempted && string.IsNullOrEmpty(_supabaseUrl))
                {
                    _autoFillAttempted = true;
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out _))
                    {
                        _supabaseUrl = url;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                    }
                }

                _serviceKey = EditorGUILayout.PasswordField(
                    new GUIContent("Service Key", "A Supabase service_role/secret key. Required: these RPCs refuse the publishable key on purpose. Never saved to disk - re-enter it each time you open this window."),
                    _serviceKey);

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                _gameSlug = EditorGUILayout.TextField("Game Slug", _gameSlug);
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefsPrefix + "Slug", _gameSlug);

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_gameSlug) || string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_serviceKey)))
                {
                    if (GUILayout.Button("Load", GUILayout.Width(70))) LoadAchievements();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);

            if (_pendingDeleteId != 0) DrawDeleteConfirmation();

            if (_achievements == null) return;

            EditorGUILayout.Space(6);
            foreach (var row in _achievements.OrderBy(r => r.Value<int>("bit_index")))
            {
                long id = row.Value<long>("id");
                string key = row.Value<string>("achievement_key");
                bool retired = row.Value<bool?>("is_retired") == true;

                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    EditorGUILayout.LabelField("bit " + row.Value<int>("bit_index"), GUILayout.Width(50));
                    EditorGUILayout.LabelField(key, GUILayout.Width(180));
                    EditorGUILayout.LabelField(row.Value<string>("title"), GUILayout.MinWidth(80));
                    if (retired) EditorGUILayout.LabelField("retired", GUILayout.Width(60));

                    using (new EditorGUI.DisabledScope(_isBusy || retired))
                    {
                        if (GUILayout.Button("Retire", GUILayout.Width(70))) Retire(id, key);
                    }
                    using (new EditorGUI.DisabledScope(_isBusy || !retired))
                    {
                        if (GUILayout.Button("Delete...", GUILayout.Width(70)))
                        {
                            _pendingDeleteId = id;
                            _pendingDeleteKey = key;
                            _deleteConfirmationText = "";
                        }
                    }
                }
            }
        }

        private void DrawDeleteConfirmation()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.HelpBox(
                    "This permanently deletes '" + _pendingDeleteKey + "' and frees its bit index for reuse. " +
                    "Only do this for a mistake that never shipped - the server will refuse if any player has " +
                    "already unlocked it. Type the achievement key to confirm.", MessageType.Warning);
                _deleteConfirmationText = EditorGUILayout.TextField("Type '" + _pendingDeleteKey + "' to confirm", _deleteConfirmationText);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_isBusy || _deleteConfirmationText != _pendingDeleteKey))
                    {
                        if (GUILayout.Button("Delete Permanently"))
                        {
                            DeleteRetired(_pendingDeleteId, _pendingDeleteKey);
                            _pendingDeleteId = 0;
                        }
                    }
                    if (GUILayout.Button("Cancel")) _pendingDeleteId = 0;
                }
            }
        }

        private void LoadAchievements()
        {
            _isBusy = true;
            SetStatus("Loading '" + _gameSlug + "'...", MessageType.Info);
            Send(_supabaseUrl.TrimEnd('/') + "/rest/v1/games?slug=eq." + UnityWebRequest.EscapeURL(_gameSlug) + "&select=id,slug", OnGameLoaded);
        }

        private void OnGameLoaded(UnityWebRequest request)
        {
            if (!TryReadSuccess(request, out string body)) return;
            var games = JArray.Parse(body);
            if (games.Count != 1)
            {
                Fail(games.Count == 0 ? "Game '" + _gameSlug + "' was not found." : "More than one game matched that slug.");
                return;
            }

            _gameId = games[0].Value<long>("id");
            Send(_supabaseUrl.TrimEnd('/') + "/rest/v1/achievements?game_id=eq." + _gameId +
                "&select=id,achievement_key,bit_index,title,is_retired&order=bit_index.asc", OnAchievementsLoaded);
        }

        private void OnAchievementsLoaded(UnityWebRequest request)
        {
            if (!TryReadSuccess(request, out string body)) return;
            _achievements = JArray.Parse(body);
            _isBusy = false;
            SetStatus("Loaded " + _achievements.Count + " achievement(s) for '" + _gameSlug + "'.", MessageType.Info);
        }

        private void Retire(long id, string key)
        {
            _isBusy = true;
            SetStatus("Retiring '" + key + "'...", MessageType.Info);
            SendRpc("retire_achievement", new JObject { ["p_achievement_id"] = id }, request =>
            {
                if (!TryReadSuccess(request, out _)) return;
                var row = _achievements.FirstOrDefault(r => r.Value<long>("id") == id) as JObject;
                if (row != null) row["is_retired"] = true;
                _isBusy = false;
                SetStatus("Retired '" + key + "'.", MessageType.Info);
            });
        }

        private void DeleteRetired(long id, string key)
        {
            _isBusy = true;
            SetStatus("Deleting '" + key + "'...", MessageType.Info);
            SendRpc("delete_retired_achievement", new JObject { ["p_achievement_id"] = id }, request =>
            {
                if (!TryReadSuccess(request, out _)) return;
                for (int i = _achievements.Count - 1; i >= 0; i--)
                    if (_achievements[i].Value<long>("id") == id) _achievements.RemoveAt(i);
                _isBusy = false;
                SetStatus("Deleted '" + key + "'.", MessageType.Info);
            });
        }

        private void SendRpc(string function, JObject body, Action<UnityWebRequest> onDone) =>
            Send(_supabaseUrl.TrimEnd('/') + "/rest/v1/rpc/" + function, onDone, body.ToString(Newtonsoft.Json.Formatting.None));

        private void Send(string url, Action<UnityWebRequest> onDone, string body = null)
        {
            var request = string.IsNullOrEmpty(body)
                ? UnityWebRequest.Get(url)
                : new UnityWebRequest(url, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                    downloadHandler = new DownloadHandlerBuffer(),
                };
            request.SetRequestHeader("apikey", _serviceKey);
            request.SetRequestHeader("Authorization", "Bearer " + _serviceKey);
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.SendWebRequest().completed += _ => onDone(request);
        }

        private bool TryReadSuccess(UnityWebRequest request, out string body)
        {
            body = null;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string detail = request.downloadHandler?.text;
                Fail("Request failed: " + request.error + (string.IsNullOrEmpty(detail) ? "" : "\n" + Truncate(detail)));
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

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }

        private static string Truncate(string text) => text != null && text.Length > 500 ? text.Substring(0, 500) + "..." : text;
    }
}
