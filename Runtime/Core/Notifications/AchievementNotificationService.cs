using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>Raised once per genuine local locked → unlocked transition.</summary>
    public readonly struct AchievementUnlockedEvent
    {
        public AchievementUnlockedEvent(AchievementDefinition definition, DateTime unlockedAtUtc)
        {
            Definition = definition;
            UnlockedAtUtc = unlockedAtUtc;
        }

        public AchievementDefinition Definition { get; }

        /// <summary>Local clock time. The server records its own timestamp on sync.</summary>
        public DateTime UnlockedAtUtc { get; }
    }

    /// <summary>
    /// One queued toast. Text starts as the fallback (or cached localized) text and may be replaced once
    /// asynchronous localization completes; presenters poll <see cref="TextVersion"/> while showing it.
    /// </summary>
    public sealed class AchievementNotification
    {
        private readonly object _gate = new object();
        private string _title;
        private string _description;
        private int _textVersion;

        internal AchievementNotification(AchievementDefinition definition, AchievementText text, bool isFinalText)
        {
            Definition = definition;
            _title = text.Title;
            _description = text.Description;
            IsTextFinal = isFinalText;
        }

        public AchievementDefinition Definition { get; }

        public string Title
        {
            get { lock (_gate) return _title; }
        }

        public string Description
        {
            get { lock (_gate) return _description; }
        }

        /// <summary>Increments whenever <see cref="Title"/>/<see cref="Description"/> change.</summary>
        public int TextVersion => Volatile.Read(ref _textVersion);

        /// <summary>False while localized text is still being resolved.</summary>
        public bool IsTextFinal { get; private set; }

        internal void ApplyText(AchievementText text)
        {
            lock (_gate)
            {
                _title = string.IsNullOrEmpty(text.Title) ? _title : text.Title;
                _description = text.Description ?? _description;
                IsTextFinal = true;
                _textVersion++;
            }
        }
    }

    /// <summary>FIFO of notifications waiting to be shown. Thread-safe; bounded so a burst cannot grow unbounded.</summary>
    public sealed class AchievementNotificationQueue
    {
        public const int DefaultCapacity = 64;

        private readonly Queue<AchievementNotification> _queue = new Queue<AchievementNotification>();
        private readonly int _capacity;

        public AchievementNotificationQueue(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        public int Count
        {
            get { lock (_queue) return _queue.Count; }
        }

        /// <summary>Returns false (dropping the notification, never the unlock) when the queue is full.</summary>
        public bool Enqueue(AchievementNotification notification)
        {
            lock (_queue)
            {
                if (_queue.Count >= _capacity) return false;
                _queue.Enqueue(notification);
                return true;
            }
        }

        public bool TryDequeue(out AchievementNotification notification)
        {
            lock (_queue)
            {
                if (_queue.Count > 0)
                {
                    notification = _queue.Dequeue();
                    return true;
                }
                notification = null;
                return false;
            }
        }

        public void Clear()
        {
            lock (_queue) _queue.Clear();
        }
    }

    /// <summary>
    /// Turns unlock events into queued notifications, honoring the presentation setting and resolving
    /// localized text without delaying the unlock. Engines render <see cref="Queue"/> however they like.
    /// </summary>
    public sealed class AchievementNotificationService
    {
        private readonly IAchievementNotificationSettingsProvider _settings;
        private readonly IAchievementLocalizationProvider _localization;
        private readonly IAchievementLogger _logger;

        public AchievementNotificationService(
            IAchievementNotificationSettingsProvider settings,
            IAchievementLocalizationProvider localization = null,
            IAchievementLogger logger = null,
            AchievementNotificationQueue queue = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _localization = localization ?? DefaultAchievementLocalizationProvider.Instance;
            _logger = logger ?? NullAchievementLogger.Instance;
            Queue = queue ?? new AchievementNotificationQueue();
        }

        public AchievementNotificationQueue Queue { get; }

        public IAchievementNotificationSettingsProvider Settings => _settings;

        /// <summary>
        /// Called by the achievement system for genuine unlocks only (never for sync or reconciliation).
        /// Returns the queued notification, or null when notifications are disabled.
        /// </summary>
        public AchievementNotification OnUnlocked(AchievementUnlockedEvent unlocked)
        {
            if (!_settings.NotificationsEnabled || unlocked.Definition == null) return null;

            var definition = unlocked.Definition;
            AchievementNotification notification;
            bool resolved;
            try
            {
                resolved = _localization.TryGetText(definition, out var text);
                notification = new AchievementNotification(definition, resolved ? text : AchievementText.Fallback(definition), resolved);
            }
            catch (Exception e)
            {
                _logger.Warning("Achievement localization failed for '" + definition.Key + "': " + e.Message);
                resolved = true;
                notification = new AchievementNotification(definition, AchievementText.Fallback(definition), true);
            }

            if (!Queue.Enqueue(notification))
            {
                _logger.Warning("Achievement notification queue is full; skipping the toast for '" + definition.Key + "' (the unlock itself is saved).");
                return null;
            }

            if (!resolved) _ = ResolveAsync(notification);
            return notification;
        }

        private async Task ResolveAsync(AchievementNotification notification)
        {
            try
            {
                var text = await _localization.GetTextAsync(notification.Definition, CancellationToken.None);
                notification.ApplyText(text);
            }
            catch (Exception e)
            {
                _logger.Warning("Achievement localization failed for '" + notification.Definition.Key + "': " + e.Message);
                notification.ApplyText(AchievementText.Fallback(notification.Definition));
            }
        }
    }
}

