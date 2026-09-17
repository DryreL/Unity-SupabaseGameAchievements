using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    internal sealed class FakeTransport : IAchievementHttpTransport
    {
        public readonly List<(string Url, string Body, Dictionary<string, string> Headers)> Requests = new List<(string, string, Dictionary<string, string>)>();
        public Func<string, string, AchievementHttpResponse> Handler = (url, body) => new AchievementHttpResponse(200, "{}");

        public Task<AchievementHttpResponse> PostJsonAsync(string url, string jsonBody, IReadOnlyList<KeyValuePair<string, string>> headers, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            Requests.Add((url, jsonBody, headers.ToDictionary(h => h.Key, h => h.Value)));
            return Task.FromResult(Handler(url, jsonBody));
        }
    }

    internal sealed class FakeSessionSource : ISupabaseSessionSource
    {
        public bool HasIdentity { get; set; } = true;
        public int Calls;
        public Func<SupabaseSessionResult> Next;
        public TaskCompletionSource<bool> Gate;

        public event Action IdentityChanged;

        public async Task<SupabaseSessionResult> CreateSessionAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Gate != null) await Gate.Task;
            return Next();
        }

        public void RaiseIdentityChanged() => IdentityChanged?.Invoke();
    }

    public class SupabaseSessionAuthProviderTests
    {
        private static readonly Guid User = new Guid("12345678-1234-1234-1234-123456789abc");
        private FakeClock _clock;
        private FakeTransport _transport;
        private FakeSessionSource _source;
        private SupabaseSessionAuthProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _transport = new FakeTransport();
            _source = new FakeSessionSource
            {
                Next = () => SupabaseSessionResult.From(new SupabaseSession("created-access", "created-refresh", _clock.UtcNow.AddHours(1), User)),
            };
            _provider = new SupabaseSessionAuthProvider("https://project.supabase.co/", "sb_publishable_test", _transport, _source, _clock);
        }

        private string SessionJson(string access, string refresh, int expiresIn = 3600) =>
            "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expires_in\":" + expiresIn + ",\"user\":{\"id\":\"" + User + "\"}}";

        [Test]
        public void Signed_out_identity_returns_no_token_without_network()
        {
            _source.HasIdentity = false;
            Assert.IsFalse(_provider.IsAuthenticated);
            Assert.IsFalse(_provider.GetAccessTokenAsync(false, CancellationToken.None).Result.IsValid);
            Assert.AreEqual(0, _source.Calls);
            Assert.AreEqual(0, _transport.Requests.Count);
        }

        [Test]
        public void Creates_a_session_once_and_reuses_it_while_fresh()
        {
            var first = _provider.GetAccessTokenAsync(false, CancellationToken.None).Result;
            _clock.Advance(30 * 60 * 1000);
            var second = _provider.GetAccessTokenAsync(false, CancellationToken.None).Result;

            Assert.AreEqual("created-access", first.AccessToken);
            Assert.AreEqual(User, first.UserId);
            Assert.AreEqual(first.AccessToken, second.AccessToken);
            Assert.AreEqual(1, _source.Calls);
            Assert.AreEqual(0, _transport.Requests.Count);
        }

        [Test]
        public void Expired_session_is_refreshed_with_the_refresh_token()
        {
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();
            _transport.Handler = (url, body) => new AchievementHttpResponse(200, SessionJson("refreshed-access", "rotated-refresh"));
            _clock.Advance(60 * 60 * 1000);

            var token = _provider.GetAccessTokenAsync(false, CancellationToken.None).Result;

            Assert.AreEqual("refreshed-access", token.AccessToken);
            Assert.AreEqual(1, _source.Calls, "no new identity exchange needed");
            Assert.AreEqual("https://project.supabase.co/auth/v1/token?grant_type=refresh_token", _transport.Requests[0].Url);
            StringAssert.Contains("created-refresh", _transport.Requests[0].Body);
            Assert.AreEqual("sb_publishable_test", _transport.Requests[0].Headers["apikey"]);
        }

        [Test]
        public void Forced_refresh_after_backend_rejection_refreshes_even_if_not_expired()
        {
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();
            _transport.Handler = (url, body) => new AchievementHttpResponse(200, SessionJson("refreshed-access", "rotated-refresh"));

            Assert.AreEqual("refreshed-access", _provider.GetAccessTokenAsync(true, CancellationToken.None).Result.AccessToken);
        }

        [Test]
        public void Rejected_refresh_token_falls_back_to_a_new_session()
        {
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();
            _transport.Handler = (url, body) => new AchievementHttpResponse(400, "{\"error\":\"invalid_grant\"}");
            _source.Next = () => SupabaseSessionResult.From(new SupabaseSession("second-access", "second-refresh", _clock.UtcNow.AddHours(1), User));

            var token = _provider.GetAccessTokenAsync(true, CancellationToken.None).Result;

            Assert.AreEqual("second-access", token.AccessToken);
            Assert.AreEqual(2, _source.Calls);
        }

        [Test]
        public void Refresh_during_outage_keeps_the_session_for_later()
        {
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();
            _transport.Handler = (url, body) => AchievementHttpResponse.NetworkError("offline");

            Assert.IsFalse(_provider.GetAccessTokenAsync(true, CancellationToken.None).Result.IsValid);
            Assert.AreEqual(1, _source.Calls, "outage does not burn a new identity exchange");

            _transport.Handler = (url, body) => new AchievementHttpResponse(200, SessionJson("after-outage", "r2"));
            Assert.AreEqual("after-outage", _provider.GetAccessTokenAsync(true, CancellationToken.None).Result.AccessToken);
        }

        [Test]
        public void Unavailable_identity_service_yields_no_token()
        {
            _source.Next = () => SupabaseSessionResult.Unavailable;
            Assert.IsFalse(_provider.GetAccessTokenAsync(false, CancellationToken.None).Result.IsValid);
        }

        [Test]
        public void Identity_change_drops_the_session_and_notifies()
        {
            int events = 0;
            _provider.AuthenticationStateChanged += () => events++;
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();

            _source.RaiseIdentityChanged();
            _provider.GetAccessTokenAsync(false, CancellationToken.None).Wait();

            Assert.AreEqual(1, events);
            Assert.AreEqual(2, _source.Calls);
        }

        [Test]
        public void Session_created_for_a_previous_identity_is_discarded()
        {
            _source.Gate = new TaskCompletionSource<bool>();
            var pending = _provider.GetAccessTokenAsync(false, CancellationToken.None);
            _source.RaiseIdentityChanged(); // player signed out/switched while the exchange was in flight
            _source.Gate.SetResult(true);

            Assert.IsFalse(pending.Result.IsValid);
        }

        [Test]
        public void Parses_middleware_and_gotrue_session_shapes()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var middleware = SupabaseSession.TryParse("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expires_at\":1767229200,\"user_id\":\"" + User + "\"}", now);
            Assert.AreEqual(User, middleware.UserId);
            Assert.AreEqual(new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), middleware.ExpiresAtUtc);

            var gotrue = SupabaseSession.TryParse(SessionJson("a", "r", 120), now);
            Assert.AreEqual(now.AddSeconds(120), gotrue.ExpiresAtUtc);

            Assert.IsNull(SupabaseSession.TryParse("{\"access_token\":\"a\"}", now), "user id is required");
            Assert.IsNull(SupabaseSession.TryParse("not json", now));
        }
    }

    public class SupabaseApiClientTests
    {
        private FakeTransport _transport;
        private SupabaseAchievementApiClient _client;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeTransport();
            _client = new SupabaseAchievementApiClient("https://project.supabase.co", "sb_publishable_test", _transport);
        }

        [Test]
        public void Sync_sends_one_rpc_with_the_whole_batch()
        {
            _transport.Handler = (url, body) => new AchievementHttpResponse(200, "{\"accepted\":[101,102],\"rejected\":[999],\"inserted\":1}");

            var response = _client.SyncAsync("user-jwt", 7, new long[] { 101, 102, 999 }, CancellationToken.None).Result;

            Assert.AreEqual(1, _transport.Requests.Count);
            var request = _transport.Requests[0];
            Assert.AreEqual("https://project.supabase.co/rest/v1/rpc/sync_achievements", request.Url);
            Assert.AreEqual("{\"p_game_id\":7,\"p_achievement_ids\":[101,102,999]}", request.Body);
            Assert.AreEqual("Bearer user-jwt", request.Headers["Authorization"]);
            Assert.AreEqual("sb_publishable_test", request.Headers["apikey"]);
            Assert.IsFalse(request.Body.Contains("user"), "never sends a user id");

            Assert.AreEqual(AchievementApiOutcome.Success, response.Outcome);
            CollectionAssert.AreEqual(new long[] { 101, 102 }, response.Accepted.ToArray());
            CollectionAssert.AreEqual(new long[] { 999 }, response.Rejected.ToArray());
            Assert.AreEqual(1, response.Inserted);
        }

        [Test]
        public void Reconciliation_reads_rpc_rows()
        {
            _transport.Handler = (url, body) => new AchievementHttpResponse(200, "[{\"achievement_id\":5,\"unlocked_at\":\"2026-09-17T10:00:00+00:00\"},{\"achievement_id\":9,\"unlocked_at\":\"2026-09-17T11:00:00+00:00\"}]");

            var response = _client.GetUnlockedAsync("user-jwt", 7, CancellationToken.None).Result;

            Assert.AreEqual("https://project.supabase.co/rest/v1/rpc/get_my_achievements", _transport.Requests[0].Url);
            Assert.AreEqual("{\"p_game_id\":7}", _transport.Requests[0].Body);
            CollectionAssert.AreEqual(new long[] { 5, 9 }, response.UnlockedIds.ToArray());
        }

        [TestCase(0, "", AchievementApiOutcome.TransientFailure)]
        [TestCase(408, "", AchievementApiOutcome.TransientFailure)]
        [TestCase(429, "", AchievementApiOutcome.TransientFailure)]
        [TestCase(500, "", AchievementApiOutcome.TransientFailure)]
        [TestCase(503, "<html>", AchievementApiOutcome.TransientFailure)]
        [TestCase(401, "{\"code\":\"PGRST303\",\"message\":\"JWT expired\"}", AchievementApiOutcome.Unauthorized)]
        [TestCase(403, "{\"code\":\"42501\",\"message\":\"not_authenticated\"}", AchievementApiOutcome.Unauthorized)]
        [TestCase(400, "{\"code\":\"P0001\",\"message\":\"unknown_game\"}", AchievementApiOutcome.UnknownGame)]
        [TestCase(400, "{\"code\":\"P0001\",\"message\":\"batch_too_large\"}", AchievementApiOutcome.PermanentFailure)]
        [TestCase(404, "{\"code\":\"PGRST202\",\"message\":\"Could not find the function\"}", AchievementApiOutcome.PermanentFailure)]
        public void Classifies_failures(int status, string body, AchievementApiOutcome expected)
        {
            _transport.Handler = (url, b) => status == 0 ? AchievementHttpResponse.NetworkError("timeout") : new AchievementHttpResponse(status, body);
            Assert.AreEqual(expected, _client.SyncAsync("t", 7, new long[] { 1 }, CancellationToken.None).Result.Outcome);
        }

        [Test]
        public void Unreadable_success_body_is_treated_as_transient_so_nothing_is_cleared()
        {
            _transport.Handler = (url, body) => new AchievementHttpResponse(200, "<html>proxy page</html>");
            var response = _client.SyncAsync("t", 7, new long[] { 1 }, CancellationToken.None).Result;
            Assert.AreEqual(AchievementApiOutcome.TransientFailure, response.Outcome);
            Assert.AreEqual(0, response.Accepted.Count);
        }
    }
}

