using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    public sealed class AchievementSystemOptions
    {
        /// <summary>Required. The game's read-only achievement manifest.</summary>
        public AchievementCatalog Catalog { get; set; }

        /// <summary>Required. Durable local state storage.</summary>
        public IAchievementStorage Storage { get; set; }

        /// <summary>Backend client. Null keeps achievements local-only (no network at all).</summary>
        public IAchievementApiClient ApiClient { get; set; }

        public IAchievementAuthProvider AuthProvider { get; set; } = OfflineAchievementAuthProvider.Instance;

        /// <summary>Null disables the notification pipeline entirely (no queue, no toasts).</summary>
        public IAchievementNotificationSettingsProvider NotificationSettings { get; set; } = new FixedAchievementNotificationSettings(true);

        public IAchievementLocalizationProvider Localization { get; set; } = DefaultAchievementLocalizationProvider.Instance;

        public IAchievementLogger Logger { get; set; } = NullAchievementLogger.Instance;

        public IAchievementClock Clock { get; set; } = SystemAchievementClock.Instance;

        /// <summary>Where system events (server merges, account switches) are delivered.</summary>
        public IAchievementDispatcher Dispatcher { get; set; } = InlineAchievementDispatcher.Instance;

        public AchievementSyncOptions Sync { get; set; } = new AchievementSyncOptions();

        /// <summary>Write saves on the thread pool. Disable on platforms without threads (WebGL).</summary>
        public bool BackgroundWrites { get; set; } = true;
    }

    /// <summary>
    /// Engine-agnostic achievement runtime for one game: catalog lookups, local unlocks, persistence,
    /// notifications and background synchronization. <see cref="AchievementManager"/> is the static
    /// facade most game code uses; this instance type exists for testing and multi-instance tools.
    /// </summary>
    /// <remarks>
    /// Threading: call <see cref="TryUnlock"/>, <see cref="Tick"/> and the query methods from the game's
    /// main thread. <see cref="AchievementUnlocked"/> is raised synchronously on the calling thread.
    /// </remarks>
    public sealed class AchievementSystem : IDisposable
    {
        private readonly AchievementStore _store;
        private readonly AchievementSyncService _sync;
        private readonly IAchievementLogger _logger;
        private readonly IAchievementClock _clock;
        private readonly HashSet<string> _warnedKeys = new HashSet<string>(StringComparer.Ordinal);
        private bool _disposed;

        public AchievementSystem(AchievementSystemOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Catalog = options.Catalog ?? throw new ArgumentException("Catalog is required.", nameof(options));
            if (options.Storage == null) throw new ArgumentException("Storage is required.", nameof(options));

            _logger = options.Logger ?? NullAchievementLogger.Instance;
            _clock = options.Clock ?? SystemAchievementClock.Instance;
            _store = new AchievementStore(Catalog, options.Storage, _logger, options.BackgroundWrites, _clock);

            if (options.NotificationSettings != null)
                Notifications = new AchievementNotificationService(options.NotificationSettings, options.Localization, _logger);

            if (options.ApiClient != null)
            {
                _sync = new AchievementSyncService(
                    Catalog, _store, options.AuthProvider ?? OfflineAchievementAuthProvider.Instance, options.ApiClient,
                    _clock, _logger, options.Dispatcher, options.Sync);
                _sync.ServerStateMerged += merged => ServerStateMerged?.Invoke(merged);
                _sync.AccountChanged += () => AccountChanged?.Invoke();
            }
        }

        /// <summary>Raised once per genuine local unlock, synchronously, before TryUnlock returns.</summary>
        public event Action<AchievementUnlockedEvent> AchievementUnlocked;

        /// <summary>Achievements learned from the server (other device, reinstall). Not an unlock notification.</summary>
        public event Action<IReadOnlyList<AchievementDefinition>> ServerStateMerged;

        /// <summary>A different signed-in account's local state became active.</summary>
        public event Action AccountChanged;

        public AchievementCatalog Catalog { get; }

        /// <summary>Null when notifications are disabled in options.</summary>
        public AchievementNotificationService Notifications { get; }

        public AchievementStore Store => _store;

        public int UnlockedCount => _store.UnlockedCount;

        public int PendingSyncCount => _store.PendingCount;

        public bool IsSyncing => _sync != null && _sync.IsSyncing;

        /// <summary>
        /// Unlocks an achievement locally. Returns true only if it was locked before this call.
        /// Never waits on the network; persistence happens in the background.
        /// </summary>
        public bool TryUnlock(string achievementKey)
        {
            if (_disposed) return false;
            if (!Catalog.TryGetByKey(achievementKey, out var definition))
            {
                WarnOnce(achievementKey, "Unknown achievement key '" + achievementKey + "'. Check the achievement manifest for " + Catalog.GameSlug + ".");
                return false;
            }
            if (definition.IsRetired)
            {
                WarnOnce(achievementKey, "Achievement '" + achievementKey + "' is retired and can no longer be unlocked.");
                return false;
            }
            if (!_store.TryUnlock(definition.BitIndex)) return false;

            _sync?.NotifyUnlocked();
            var unlocked = new AchievementUnlockedEvent(definition, _clock.UtcNow);

            try
            {
                Notifications?.OnUnlocked(unlocked);
            }
            catch (Exception e)
            {
                _logger.Error("Achievement notification failed: " + e);
            }

            var handlers = AchievementUnlocked;
            if (handlers != null)
            {
                try
                {
                    handlers(unlocked);
                }
                catch (Exception e)
                {
                    _logger.Error("AchievementUnlocked handler threw: " + e);
                }
            }
            return true;
        }

        public bool HasUnlocked(string achievementKey) =>
            Catalog.TryGetByKey(achievementKey, out var definition) && _store.IsUnlocked(definition.BitIndex);

        public bool IsPendingSync(string achievementKey) =>
            Catalog.TryGetByKey(achievementKey, out var definition) && _store.IsPending(definition.BitIndex);

        /// <summary>Returns the definition, or null for an unknown key.</summary>
        public AchievementDefinition GetDefinition(string achievementKey) =>
            Catalog.TryGetByKey(achievementKey, out var definition) ? definition : null;

        public IReadOnlyList<AchievementDefinition> GetDefinitions() => Catalog.Achievements;

        /// <summary>Drives background sync and save retries. Call every frame from the main loop.</summary>
        public void Tick()
        {
            if (_disposed) return;
            _store.RetryFailedSave();
            _sync?.Tick();
        }

        public Task<AchievementSyncResult> SyncAsync(CancellationToken cancellationToken = default) =>
            _sync != null ? _sync.SyncAsync(cancellationToken) : Task.FromResult(new AchievementSyncResult(AchievementSyncStatus.Disabled));

        public Task<AchievementSyncResult> SyncServerStateAsync(CancellationToken cancellationToken = default) =>
            _sync != null ? _sync.SyncServerStateAsync(cancellationToken) : Task.FromResult(new AchievementSyncResult(AchievementSyncStatus.Disabled));

        public void NotifyNetworkAvailable() => _sync?.NotifyNetworkAvailable();

        /// <summary>App is being suspended or losing focus: persist now and sync at the next opportunity.</summary>
        public void OnApplicationPausing()
        {
            _store.Flush(500);
            _sync?.RequestImmediateSync();
            Tick();
        }

        /// <summary>Flushes unsaved state and stops background synchronization.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            // Best effort final upload: the batch is snapshotted synchronously here; if the process lives
            // long enough the request completes, otherwise the still-pending bits sync next launch.
            _sync?.RequestImmediateSync();
            _sync?.Tick();
            _disposed = true;
            _sync?.Dispose();
            _store.Close(2000);
        }

        private void WarnOnce(string key, string message)
        {
            lock (_warnedKeys)
            {
                if (!_warnedKeys.Add(key ?? string.Empty)) return;
            }
            _logger.Warning(message);
        }
    }
}

