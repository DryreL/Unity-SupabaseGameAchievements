using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    public readonly struct AchievementText
    {
        public AchievementText(string title, string description)
        {
            Title = title;
            Description = description;
        }

        public string Title { get; }

        public string Description { get; }

        public static AchievementText Fallback(AchievementDefinition definition) =>
            new AchievementText(definition.Title, definition.Description);
    }

    /// <summary>
    /// Optional localization of achievement titles/descriptions. The core never depends on a specific
    /// localization system; engines plug one in (see UnityLocalizationProvider).
    /// </summary>
    public interface IAchievementLocalizationProvider
    {
        /// <summary>
        /// Fast path: returns true with the final text if it is available without waiting (cached, or no
        /// localization configured). Must not block.
        /// </summary>
        bool TryGetText(AchievementDefinition definition, out AchievementText text);

        /// <summary>
        /// Resolves localized text. Never throws: missing tables/entries or load failures resolve to the
        /// definition's fallback text for the affected field.
        /// </summary>
        Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken);
    }

    /// <summary>Uses the manifest's fallback title/description. The default when no localization is configured.</summary>
    public sealed class DefaultAchievementLocalizationProvider : IAchievementLocalizationProvider
    {
        public static readonly DefaultAchievementLocalizationProvider Instance = new DefaultAchievementLocalizationProvider();

        public bool TryGetText(AchievementDefinition definition, out AchievementText text)
        {
            text = AchievementText.Fallback(definition);
            return true;
        }

        public Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken) =>
            Task.FromResult(AchievementText.Fallback(definition));
    }
}

