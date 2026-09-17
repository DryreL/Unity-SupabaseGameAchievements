using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements.Samples
{
    /// <summary>
    /// Turns a token from the game's existing sign-in system into a Supabase session by POSTing it to a
    /// server endpoint that verifies it and mints the session (for example the <c>supabase-session</c>
    /// action of an Edge Function). The achievement core never sees the identity provider.
    /// </summary>
    public sealed class ExternalIdentitySessionSource : ISupabaseSessionSource
    {
        private readonly string _endpointUrl;
        private readonly string _publishableKey;
        private readonly IAchievementHttpTransport _transport;
        private readonly Func<bool> _hasIdentity;
        private readonly Func<string> _getIdentityToken;
        private readonly string _tokenFieldName;
        private readonly IAchievementClock _clock;

        /// <param name="endpointUrl">e.g. https://PROJECT.supabase.co/functions/v1/patreon-middleware?action=supabase-session</param>
        /// <param name="hasIdentity">Cheap check: is the player signed in to the identity provider?</param>
        /// <param name="getIdentityToken">Returns a currently valid identity access token, or null.</param>
        /// <param name="tokenFieldName">JSON field the endpoint expects, e.g. "patreon_access_token".</param>
        public ExternalIdentitySessionSource(
            string endpointUrl,
            string publishableKey,
            IAchievementHttpTransport transport,
            Func<bool> hasIdentity,
            Func<string> getIdentityToken,
            string tokenFieldName,
            IAchievementClock clock = null)
        {
            _endpointUrl = endpointUrl;
            _publishableKey = publishableKey;
            _transport = transport;
            _hasIdentity = hasIdentity;
            _getIdentityToken = getIdentityToken;
            _tokenFieldName = tokenFieldName;
            _clock = clock ?? SystemAchievementClock.Instance;
        }

        public event Action IdentityChanged;

        public bool HasIdentity => _hasIdentity();

        /// <summary>Call from the identity system's sign-in / sign-out / account-changed callbacks.</summary>
        public void NotifyIdentityChanged() => IdentityChanged?.Invoke();

        public async Task<SupabaseSessionResult> CreateSessionAsync(CancellationToken cancellationToken)
        {
            string identityToken = _getIdentityToken();
            if (string.IsNullOrEmpty(identityToken)) return SupabaseSessionResult.Unavailable;

            var headers = new[]
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + _publishableKey),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            string body = new JObject { [_tokenFieldName] = identityToken }.ToString(Newtonsoft.Json.Formatting.None);
            var response = await _transport.PostJsonAsync(_endpointUrl, body, headers, 20000, cancellationToken);

            // 401: identity token rejected (the sign-in system should refresh it); 429/5xx/0: try later.
            return response.IsSuccess
                ? SupabaseSessionResult.From(SupabaseSession.TryParse(response.Body, _clock.UtcNow))
                : SupabaseSessionResult.Unavailable;
        }
    }
}

