using System;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Supplies backend access tokens to the achievement system. The achievement system knows
    /// nothing about how the player signs in; the game/launcher integration layer implements this.
    /// </summary>
    public interface IAchievementAuthProvider
    {
        /// <summary>
        /// True when the player has an identity that can (probably) produce a token. Cheap; called
        /// every frame by the sync scheduler, so it must not do I/O.
        /// </summary>
        bool IsAuthenticated { get; }

        /// <summary>
        /// Returns a valid access token, or <see cref="AchievementAuthToken.None"/> when none can be
        /// obtained right now. Must not throw for expected failures (offline, signed out, expired).
        /// </summary>
        /// <param name="forceRefresh">True after the backend rejected the previously returned token.</param>
        Task<AchievementAuthToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);

        /// <summary>Raised (on any thread) when sign-in state or the signed-in account changes.</summary>
        event Action AuthenticationStateChanged;
    }

    public readonly struct AchievementAuthToken
    {
        public static readonly AchievementAuthToken None = default;

        public AchievementAuthToken(string accessToken, Guid userId)
        {
            AccessToken = accessToken;
            UserId = userId;
        }

        public string AccessToken { get; }

        /// <summary>The backend user id the token represents (Supabase: auth.uid()).</summary>
        public Guid UserId { get; }

        public bool IsValid => !string.IsNullOrEmpty(AccessToken) && UserId != Guid.Empty;
    }

    /// <summary>Auth provider for games that never sign in: achievements stay local-only.</summary>
    public sealed class OfflineAchievementAuthProvider : IAchievementAuthProvider
    {
        public static readonly OfflineAchievementAuthProvider Instance = new OfflineAchievementAuthProvider();

        public bool IsAuthenticated => false;

        public Task<AchievementAuthToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(AchievementAuthToken.None);

        public event Action AuthenticationStateChanged
        {
            add { }
            remove { }
        }
    }
}

