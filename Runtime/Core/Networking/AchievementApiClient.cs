using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>Backend operations used by the sync service. One HTTP request per call.</summary>
    public interface IAchievementApiClient
    {
        /// <summary>Idempotently uploads a batch of unlocked achievement ids for the token's user.</summary>
        Task<AchievementSyncResponse> SyncAsync(string accessToken, long gameId, IReadOnlyList<long> achievementIds, CancellationToken cancellationToken);

        /// <summary>Fetches every achievement id the token's user has unlocked in the game.</summary>
        Task<AchievementFetchResponse> GetUnlockedAsync(string accessToken, long gameId, CancellationToken cancellationToken);
    }

    public enum AchievementApiOutcome
    {
        Success,

        /// <summary>Token missing/expired/rejected (HTTP 401/403). Refresh and retry once.</summary>
        Unauthorized,

        /// <summary>Offline, timeout, 408, 429, 5xx. Keep pending data; retry with backoff.</summary>
        TransientFailure,

        /// <summary>The server does not know this game id (catalog mismatch). Keep pending data; stop retrying this session.</summary>
        UnknownGame,

        /// <summary>Any other rejection (e.g. backend not deployed). Keep pending data; retry rarely.</summary>
        PermanentFailure,
    }

    public sealed class AchievementSyncResponse
    {
        private static readonly long[] Empty = new long[0];

        public AchievementSyncResponse(AchievementApiOutcome outcome, IReadOnlyList<long> accepted = null, IReadOnlyList<long> rejected = null, int inserted = 0, string error = null)
        {
            Outcome = outcome;
            Accepted = accepted ?? Empty;
            Rejected = rejected ?? Empty;
            Inserted = inserted;
            Error = error;
        }

        public AchievementApiOutcome Outcome { get; }

        /// <summary>Stored on the server (now or earlier). Safe to clear from the pending queue.</summary>
        public IReadOnlyList<long> Accepted { get; }

        /// <summary>Unknown, retired, or belonging to another game. Permanent; also cleared from the queue.</summary>
        public IReadOnlyList<long> Rejected { get; }

        public int Inserted { get; }

        public string Error { get; }
    }

    public sealed class AchievementFetchResponse
    {
        private static readonly long[] Empty = new long[0];

        public AchievementFetchResponse(AchievementApiOutcome outcome, IReadOnlyList<long> unlockedIds = null, string error = null)
        {
            Outcome = outcome;
            UnlockedIds = unlockedIds ?? Empty;
            Error = error;
        }

        public AchievementApiOutcome Outcome { get; }

        public IReadOnlyList<long> UnlockedIds { get; }

        public string Error { get; }
    }

    /// <summary>
    /// <see cref="IAchievementApiClient"/> for the Supabase schema in
    /// <c>supabase/migrations/*_achievements.sql</c>: two PostgREST RPC calls, authorized by the user's
    /// Supabase access token plus the project's publishable (anon) key.
    /// </summary>
    public sealed class SupabaseAchievementApiClient : IAchievementApiClient
    {
        public const int DefaultTimeoutMilliseconds = 15000;

        private readonly string _rpcBaseUrl;
        private readonly string _publishableKey;
        private readonly IAchievementHttpTransport _transport;
        private readonly int _timeoutMs;

        public SupabaseAchievementApiClient(string supabaseUrl, string publishableKey, IAchievementHttpTransport transport, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
        {
            if (string.IsNullOrEmpty(supabaseUrl)) throw new ArgumentException("Supabase URL is required.", nameof(supabaseUrl));
            if (string.IsNullOrEmpty(publishableKey)) throw new ArgumentException("Supabase publishable key is required.", nameof(publishableKey));
            _rpcBaseUrl = supabaseUrl.TrimEnd('/') + "/rest/v1/rpc/";
            _publishableKey = publishableKey;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _timeoutMs = timeoutMilliseconds;
        }

        public async Task<AchievementSyncResponse> SyncAsync(string accessToken, long gameId, IReadOnlyList<long> achievementIds, CancellationToken cancellationToken)
        {
            var body = new StringBuilder(48 + achievementIds.Count * 8);
            body.Append("{\"p_game_id\":").Append(gameId.ToString(CultureInfo.InvariantCulture)).Append(",\"p_achievement_ids\":[");
            for (int i = 0; i < achievementIds.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append(achievementIds[i].ToString(CultureInfo.InvariantCulture));
            }
            body.Append("]}");

            var response = await _transport.PostJsonAsync(_rpcBaseUrl + "sync_achievements", body.ToString(), Headers(accessToken), _timeoutMs, cancellationToken);
            var outcome = Classify(response, out string error);
            if (outcome != AchievementApiOutcome.Success) return new AchievementSyncResponse(outcome, error: error);

            try
            {
                var json = JObject.Parse(response.Body);
                return new AchievementSyncResponse(
                    AchievementApiOutcome.Success,
                    ReadIds(json["accepted"]),
                    ReadIds(json["rejected"]),
                    json.Value<int?>("inserted") ?? 0);
            }
            catch (Exception e) when (e is JsonException || e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                // A 200 we cannot understand: treat as transient so nothing is cleared.
                return new AchievementSyncResponse(AchievementApiOutcome.TransientFailure, error: "Unreadable sync response: " + e.Message);
            }
        }

        public async Task<AchievementFetchResponse> GetUnlockedAsync(string accessToken, long gameId, CancellationToken cancellationToken)
        {
            string body = "{\"p_game_id\":" + gameId.ToString(CultureInfo.InvariantCulture) + "}";
            var response = await _transport.PostJsonAsync(_rpcBaseUrl + "get_my_achievements", body, Headers(accessToken), _timeoutMs, cancellationToken);
            var outcome = Classify(response, out string error);
            if (outcome != AchievementApiOutcome.Success) return new AchievementFetchResponse(outcome, error: error);

            try
            {
                var rows = JArray.Parse(response.Body);
                var ids = new List<long>(rows.Count);
                foreach (var row in rows)
                {
                    var id = row["achievement_id"];
                    if (id != null && id.Type == JTokenType.Integer) ids.Add((long)id);
                }
                return new AchievementFetchResponse(AchievementApiOutcome.Success, ids);
            }
            catch (Exception e) when (e is JsonException || e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                return new AchievementFetchResponse(AchievementApiOutcome.TransientFailure, error: "Unreadable reconciliation response: " + e.Message);
            }
        }

        private IReadOnlyList<KeyValuePair<string, string>> Headers(string accessToken) => new[]
        {
            new KeyValuePair<string, string>("apikey", _publishableKey),
            new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
            new KeyValuePair<string, string>("Accept", "application/json"),
        };

        internal static AchievementApiOutcome Classify(AchievementHttpResponse response, out string error)
        {
            error = null;
            int status = response.StatusCode;
            if (response.IsSuccess) return AchievementApiOutcome.Success;

            string message = ReadErrorMessage(response.Body);
            error = status == 0 ? "Network error: " + response.Error : "HTTP " + status + (message != null ? ": " + message : string.Empty);

            if (status == 0 || status == 408 || status == 425 || status == 429 || status >= 500) return AchievementApiOutcome.TransientFailure;
            if (status == 401 || status == 403) return AchievementApiOutcome.Unauthorized;
            if (status == 400 && message == "unknown_game") return AchievementApiOutcome.UnknownGame;
            return AchievementApiOutcome.PermanentFailure;
        }

        private static string ReadErrorMessage(string body)
        {
            if (string.IsNullOrEmpty(body) || body[0] != '{') return null;
            try
            {
                return JObject.Parse(body).Value<string>("message");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IReadOnlyList<long> ReadIds(JToken token)
        {
            if (!(token is JArray array)) return new long[0];
            var ids = new long[array.Count];
            for (int i = 0; i < array.Count; i++) ids[i] = (long)array[i];
            return ids;
        }
    }
}

