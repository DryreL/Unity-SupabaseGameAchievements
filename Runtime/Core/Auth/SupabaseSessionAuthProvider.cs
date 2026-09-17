using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>A Supabase Auth session. Kept in memory only; never written to the achievement save.</summary>
    public sealed class SupabaseSession
    {
        public SupabaseSession(string accessToken, string refreshToken, DateTime expiresAtUtc, Guid userId)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            ExpiresAtUtc = expiresAtUtc;
            UserId = userId;
        }

        public string AccessToken { get; }

        public string RefreshToken { get; }

        public DateTime ExpiresAtUtc { get; }

        public Guid UserId { get; }

        /// <summary>
        /// Parses a GoTrue token response (<c>/auth/v1/token</c>, <c>/auth/v1/verify</c>) or the
        /// <c>supabase-session</c> middleware response. Returns null if required fields are missing.
        /// </summary>
        public static SupabaseSession TryParse(string json, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var root = JObject.Parse(json);
                string accessToken = root.Value<string>("access_token");
                string refreshToken = root.Value<string>("refresh_token");
                string userIdText = root.Value<string>("user_id") ?? (root["user"] as JObject)?.Value<string>("id");
                if (string.IsNullOrEmpty(accessToken) || !Guid.TryParse(userIdText, out var userId)) return null;

                long? expiresAt = root.Value<long?>("expires_at");
                long? expiresIn = root.Value<long?>("expires_in");
                DateTime expiry = expiresAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value).UtcDateTime
                    : nowUtc.AddSeconds(expiresIn ?? 3600);
                return new SupabaseSession(accessToken, refreshToken, expiry, userId);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public enum SupabaseSessionStatus
    {
        Success,

        /// <summary>No identity right now, or the identity service is unreachable. Try again later.</summary>
        Unavailable,
    }

    public readonly struct SupabaseSessionResult
    {
        public SupabaseSessionResult(SupabaseSessionStatus status, SupabaseSession session)
        {
            Status = status;
            Session = session;
        }

        public SupabaseSessionStatus Status { get; }

        public SupabaseSession Session { get; }

        public static SupabaseSessionResult Unavailable => new SupabaseSessionResult(SupabaseSessionStatus.Unavailable, null);

        public static SupabaseSessionResult From(SupabaseSession session) =>
            session != null ? new SupabaseSessionResult(SupabaseSessionStatus.Success, session) : Unavailable;
    }

    /// <summary>
    /// Integration hook that turns the game's own sign-in (whatever it is) into a fresh Supabase
    /// session. Called only when there is no usable session or refresh token.
    /// </summary>
    public interface ISupabaseSessionSource
    {
        /// <summary>Cheap, no I/O: is a player signed in to the underlying identity system?</summary>
        bool HasIdentity { get; }

        /// <summary>Raised when the player signs in, signs out, or switches account.</summary>
        event Action IdentityChanged;

        Task<SupabaseSessionResult> CreateSessionAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Caches a Supabase session in memory, refreshes it with the standard refresh-token grant, and
    /// falls back to <see cref="ISupabaseSessionSource"/> when refreshing is impossible. Concurrent
    /// callers share one in-flight refresh.
    /// </summary>
    public sealed class SupabaseSessionAuthProvider : IAchievementAuthProvider, IDisposable
    {
        private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

        private readonly string _refreshUrl;
        private readonly string _publishableKey;
        private readonly IAchievementHttpTransport _transport;
        private readonly ISupabaseSessionSource _source;
        private readonly IAchievementClock _clock;
        private readonly IAchievementLogger _logger;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly object _sessionGate = new object();

        private SupabaseSession _session;
        private int _identityGeneration;

        public SupabaseSessionAuthProvider(
            string supabaseUrl,
            string publishableKey,
            IAchievementHttpTransport transport,
            ISupabaseSessionSource source,
            IAchievementClock clock = null,
            IAchievementLogger logger = null)
        {
            if (string.IsNullOrEmpty(supabaseUrl)) throw new ArgumentException("Supabase URL is required.", nameof(supabaseUrl));
            _refreshUrl = supabaseUrl.TrimEnd('/') + "/auth/v1/token?grant_type=refresh_token";
            _publishableKey = publishableKey ?? throw new ArgumentNullException(nameof(publishableKey));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _clock = clock ?? SystemAchievementClock.Instance;
            _logger = logger ?? NullAchievementLogger.Instance;
            _source.IdentityChanged += OnIdentityChanged;
        }

        public event Action AuthenticationStateChanged;

        public bool IsAuthenticated => _source.HasIdentity;

        public async Task<AchievementAuthToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!_source.HasIdentity) return AchievementAuthToken.None;

            SupabaseSession cached;
            lock (_sessionGate) cached = _session;
            if (!forceRefresh && IsFresh(cached)) return ToToken(cached);

            try
            {
                await _refreshGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return AchievementAuthToken.None;
            }

            try
            {
                int generation;
                lock (_sessionGate)
                {
                    generation = _identityGeneration;
                    // Another caller may have refreshed while we waited.
                    if (_session != null && _session != cached && IsFresh(_session)) return ToToken(_session);
                    cached = _session;
                }

                if (cached != null && !string.IsNullOrEmpty(cached.RefreshToken))
                {
                    var refreshed = await RefreshAsync(cached.RefreshToken, cancellationToken);
                    if (refreshed.Session != null) return Store(refreshed.Session, generation);
                    if (refreshed.Transient)
                    {
                        // Keep the refresh token for later. A still-valid access token is better than none,
                        // unless the backend has just rejected it.
                        return !forceRefresh && IsFresh(cached) ? ToToken(cached) : AchievementAuthToken.None;
                    }
                    lock (_sessionGate)
                    {
                        if (_session == cached) _session = null;
                    }
                }

                SupabaseSessionResult created;
                try
                {
                    created = await _source.CreateSessionAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return AchievementAuthToken.None;
                }
                catch (Exception e)
                {
                    _logger.Warning("Could not create a backend session: " + e.Message);
                    return AchievementAuthToken.None;
                }

                return created.Status == SupabaseSessionStatus.Success && created.Session != null
                    ? Store(created.Session, generation)
                    : AchievementAuthToken.None;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void Dispose()
        {
            _source.IdentityChanged -= OnIdentityChanged;
        }

        private void OnIdentityChanged()
        {
            lock (_sessionGate)
            {
                _session = null;
                _identityGeneration++;
            }
            AuthenticationStateChanged?.Invoke();
        }

        private AchievementAuthToken Store(SupabaseSession session, int generation)
        {
            lock (_sessionGate)
            {
                // The player signed out or switched account while this request was in flight.
                if (generation != _identityGeneration) return AchievementAuthToken.None;
                _session = session;
            }
            return ToToken(session);
        }

        private bool IsFresh(SupabaseSession session) =>
            session != null && session.ExpiresAtUtc - ExpirySkew > _clock.UtcNow;

        private static AchievementAuthToken ToToken(SupabaseSession session) => new AchievementAuthToken(session.AccessToken, session.UserId);

        private async Task<(SupabaseSession Session, bool Transient)> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            var headers = new[]
            {
                new KeyValuePair<string, string>("apikey", _publishableKey),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            string body = new JObject { ["refresh_token"] = refreshToken }.ToString(Newtonsoft.Json.Formatting.None);
            var response = await _transport.PostJsonAsync(_refreshUrl, body, headers, 15000, cancellationToken);

            if (response.IsSuccess)
            {
                var session = SupabaseSession.TryParse(response.Body, _clock.UtcNow);
                return session != null ? (session, false) : (null, true);
            }

            bool transient = response.StatusCode == 0 || response.StatusCode == 408 || response.StatusCode == 429 || response.StatusCode >= 500;
            if (!transient) _logger.Info("Backend refresh token was rejected (HTTP " + response.StatusCode + "); requesting a new session.");
            return (null, transient);
        }
    }
}

