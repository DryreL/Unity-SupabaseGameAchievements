using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Static entry point for gameplay code.
    /// <code>
    /// AchievementManager.TryUnlock("first_blood");
    /// if (AchievementManager.HasUnlocked("first_blood")) { ... }
    /// </code>
    /// Engine hosts (e.g. UnityAchievementManager) call <see cref="Initialize"/> and pump <see cref="Tick"/>.
    /// Every call is safe before initialization: queries return false/null and unlocks are ignored
    /// with a warning.
    /// </summary>
    public static class AchievementManager
    {
        private static AchievementSystem _system;
        private static Action<AchievementUnlockedEvent> _unlocked;
        private static Action<IReadOnlyList<AchievementDefinition>> _serverStateMerged;
        private static bool _warnedUninitialized;

        /// <summary>Raised once per genuine local unlock. Subscriptions survive re-initialization.</summary>
        public static event Action<AchievementUnlockedEvent> AchievementUnlocked
        {
            add => _unlocked += value;
            remove => _unlocked -= value;
        }

        /// <summary>Raised when reconciliation learns unlocks from the server. Not a notification trigger.</summary>
        public static event Action<IReadOnlyList<AchievementDefinition>> ServerStateMerged
        {
            add => _serverStateMerged += value;
            remove => _serverStateMerged -= value;
        }

        /// <summary>Logger for facade-level warnings (e.g. calls before initialization). Set by engine hosts.</summary>
        public static IAchievementLogger Logger { get; set; } = NullAchievementLogger.Instance;

        public static bool IsInitialized => _system != null;

        /// <summary>The active instance, or null before initialization.</summary>
        public static AchievementSystem Current => _system;

        public static AchievementSystem Initialize(AchievementSystemOptions options)
        {
            var system = new AchievementSystem(options);
            system.AchievementUnlocked += e => _unlocked?.Invoke(e);
            system.ServerStateMerged += merged => _serverStateMerged?.Invoke(merged);

            var previous = Interlocked.Exchange(ref _system, system);
            previous?.Dispose();
            _warnedUninitialized = false;
            return system;
        }

        public static bool TryUnlock(string achievementKey)
        {
            var system = _system;
            if (system == null)
            {
                if (!_warnedUninitialized)
                {
                    _warnedUninitialized = true;
                    (Logger ?? NullAchievementLogger.Instance).Warning("TryUnlock('" + achievementKey + "') called before AchievementManager.Initialize; ignored.");
                }
                return false;
            }
            return system.TryUnlock(achievementKey);
        }

        public static bool HasUnlocked(string achievementKey) => _system != null && _system.HasUnlocked(achievementKey);

        public static AchievementDefinition GetDefinition(string achievementKey) => _system?.GetDefinition(achievementKey);

        public static IReadOnlyList<AchievementDefinition> GetDefinitions() =>
            _system != null ? _system.GetDefinitions() : (IReadOnlyList<AchievementDefinition>)Array.Empty<AchievementDefinition>();

        public static int UnlockedCount => _system?.UnlockedCount ?? 0;

        public static int PendingSyncCount => _system?.PendingSyncCount ?? 0;

        public static void Tick() => _system?.Tick();

        public static Task<AchievementSyncResult> SyncAsync(CancellationToken cancellationToken = default) =>
            _system != null ? _system.SyncAsync(cancellationToken) : Task.FromResult(new AchievementSyncResult(AchievementSyncStatus.Disabled));

        public static Task<AchievementSyncResult> SyncServerStateAsync(CancellationToken cancellationToken = default) =>
            _system != null ? _system.SyncServerStateAsync(cancellationToken) : Task.FromResult(new AchievementSyncResult(AchievementSyncStatus.Disabled));

        public static void Shutdown()
        {
            Interlocked.Exchange(ref _system, null)?.Dispose();
        }
    }
}

