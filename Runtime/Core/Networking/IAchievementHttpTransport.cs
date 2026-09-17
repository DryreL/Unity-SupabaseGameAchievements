using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Minimal HTTP abstraction so the core stays engine-agnostic. Unity uses UnityWebRequest; tools and
    /// tests can use HttpClient. Implementations must not throw for network failures or timeouts;
    /// they return <see cref="AchievementHttpResponse.StatusCode"/> 0 instead.
    /// </summary>
    public interface IAchievementHttpTransport
    {
        Task<AchievementHttpResponse> PostJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            int timeoutMilliseconds,
            CancellationToken cancellationToken);
    }

    public readonly struct AchievementHttpResponse
    {
        public AchievementHttpResponse(int statusCode, string body, string error = null)
        {
            StatusCode = statusCode;
            Body = body;
            Error = error;
        }

        /// <summary>HTTP status, or 0 when no response was received (offline, DNS, TLS, timeout).</summary>
        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>Transport-level error description when <see cref="StatusCode"/> is 0.</summary>
        public string Error { get; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

        public static AchievementHttpResponse NetworkError(string error) => new AchievementHttpResponse(0, null, error);
    }
}

