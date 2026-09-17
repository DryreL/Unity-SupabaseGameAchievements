using System;
using System.Collections.Generic;
using System.Threading;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Thread-safe owner of the local <see cref="AchievementState"/>: unlock bits, pending-sync bits,
    /// the account the state belongs to, and coalesced crash-safe persistence.
    /// </summary>
    /// <remarks>
    /// Locking: <c>_gate</c> guards in-memory state and is held only for a few bit operations, so
    /// gameplay calls never wait on disk. <c>_ioGate</c> serializes writes so snapshots reach disk in
    /// the order they were taken. Lock order is always <c>_ioGate</c> → <c>_gate</c>.
    ///
    /// Account ownership: the state is "sticky" to the last account that synchronized it. Unlocks made
    /// while signed out (or while auth is temporarily unavailable) belong to that account. When a
    /// different account authenticates, the previous account's state is archived to its own slot and
    /// the new account's archive (if any) becomes active. Never-claimed (guest) state is adopted by the
    /// first account that signs in, which is how offline unlocks reach the server after login.
    /// </remarks>
    public sealed class AchievementStore
    {
        public const string ActiveSlot = "active";
        private const long SaveRetryDelayMs = 5000;

        private readonly object _gate = new object();
        private readonly object _ioGate = new object();
        private readonly AchievementCatalog _catalog;
        private readonly IAchievementStorage _storage;
        private readonly IAchievementLogger _logger;
        private readonly IAchievementClock _clock;
        private readonly bool _backgroundWrites;

        private AchievementState _state;
        private int _unlockedCount;
        private int _pendingCount;
        private int _epoch;

        private long _dirtyGeneration;
        private long _savedGeneration;
        private bool _saveRunning;
        private bool _saveFailed;
        private long _lastSaveFailureMs;
        private bool _loadBlocked;   // IoError at load: retry the load before the first write
        private bool _readOnly;      // newer on-disk format: never overwrite during this session
        private bool _closed;        // superseded/disposed: late results must not touch memory or disk

        public AchievementStore(
            AchievementCatalog catalog,
            IAchievementStorage storage,
            IAchievementLogger logger = null,
            bool backgroundWrites = true,
            IAchievementClock clock = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? NullAchievementLogger.Instance;
            _clock = clock ?? SystemAchievementClock.Instance;
            _backgroundWrites = backgroundWrites;
            LoadInitial();
        }

        public int UnlockedCount => Volatile.Read(ref _unlockedCount);

        /// <summary>Pending unlocks this build can synchronize (known to its catalog).</summary>
        public int PendingCount => Volatile.Read(ref _pendingCount);

        /// <summary>Changes whenever the active account state is swapped. Guards in-flight sync results.</summary>
        public int Epoch => Volatile.Read(ref _epoch);

        public bool IsReadOnly => _readOnly;

        public bool HasUnsavedChanges
        {
            get { lock (_gate) return _dirtyGeneration != _savedGeneration; }
        }

        public Guid OwnerId
        {
            get { lock (_gate) return _state.OwnerId; }
        }

        public bool IsUnlocked(int bitIndex)
        {
            lock (_gate) return _state.IsUnlocked(bitIndex);
        }

        public bool IsPending(int bitIndex)
        {
            lock (_gate) return _state.IsPending(bitIndex);
        }

        /// <summary>Sets unlocked + pending bits. Returns true only on a genuine locked → unlocked transition.</summary>
        public bool TryUnlock(int bitIndex)
        {
            lock (_gate)
            {
                if (_closed || !_state.SetUnlocked(bitIndex)) return false;
                _unlockedCount++;
                if (_state.SetPending(bitIndex)) _pendingCount++;
                _dirtyGeneration++;
            }
            RequestSave();
            return true;
        }

        /// <summary>
        /// Copies up to <paramref name="max"/> pending achievement ids known to the catalog into
        /// <paramref name="ids"/>. Pending bits this build's catalog does not know (written by a newer
        /// build) stay pending and are left for that build to sync.
        /// </summary>
        public int SnapshotPending(List<long> ids, int max, out int epoch)
        {
            ids.Clear();
            lock (_gate)
            {
                epoch = _epoch;
                int capacity = _state.BitCapacity;
                for (int bit = 0; bit < capacity && ids.Count < max; bit++)
                {
                    if ((bit & 7) == 0 && !AnyPendingInByte(bit)) { bit += 7; continue; }
                    if (_state.IsPending(bit) && _catalog.TryGetByBit(bit, out var definition))
                        ids.Add(definition.Id);
                }
            }
            return ids.Count;
        }

        private bool AnyPendingInByte(int firstBit)
        {
            for (int i = 0; i < 8; i++)
                if (_state.IsPending(firstBit + i)) return true;
            return false;
        }

        /// <summary>
        /// Clears pending bits for ids the server confirmed (accepted or permanently rejected).
        /// Ignored if the active account changed since the snapshot was taken.
        /// </summary>
        public int ClearPending(int epoch, IEnumerable<long> ids)
        {
            int cleared = 0;
            lock (_gate)
            {
                if (_closed || epoch != _epoch) return 0;
                foreach (long id in ids)
                {
                    if (_catalog.TryGetById(id, out var definition) && _state.ClearPending(definition.BitIndex))
                    {
                        _pendingCount--;
                        cleared++;
                    }
                }
                if (cleared > 0) _dirtyGeneration++;
            }
            if (cleared > 0) RequestSave();
            return cleared;
        }

        /// <summary>
        /// Merges server-known unlocks. Sets unlocked bits (never clears one) and clears pending bits the
        /// server already has. Adds bits that became unlocked to <paramref name="newlyUnlocked"/>.
        /// Does not raise unlock notifications — that is the caller's contract.
        /// </summary>
        public int MergeServerUnlocks(int epoch, IEnumerable<long> serverIds, List<AchievementDefinition> newlyUnlocked)
        {
            int changed = 0;
            lock (_gate)
            {
                if (_closed || epoch != _epoch) return 0;
                foreach (long id in serverIds)
                {
                    if (!_catalog.TryGetById(id, out var definition)) continue; // newer than this build
                    bool dirty = false;
                    if (_state.SetUnlocked(definition.BitIndex))
                    {
                        _unlockedCount++;
                        newlyUnlocked?.Add(definition);
                        dirty = true;
                    }
                    if (_state.ClearPending(definition.BitIndex))
                    {
                        _pendingCount--;
                        dirty = true;
                    }
                    if (dirty) changed++;
                }
                if (changed > 0) _dirtyGeneration++;
            }
            if (changed > 0) RequestSave();
            return changed;
        }

        /// <summary>Binds the state to <paramref name="userId"/>, archiving another account's state if needed.</summary>
        public AchievementOwnerChange EnsureOwner(Guid userId)
        {
            if (userId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(userId));

            bool claimed = false;
            lock (_gate)
            {
                if (_closed) return AchievementOwnerChange.Failed;
                if (_state.OwnerId == userId) return AchievementOwnerChange.Unchanged;
                if (_state.OwnerId == Guid.Empty)
                {
                    _state.OwnerId = userId;
                    _dirtyGeneration++;
                    claimed = true;
                }
            }
            if (claimed)
            {
                RequestSave();
                return AchievementOwnerChange.Claimed;
            }

            lock (_ioGate)
            {
                lock (_gate)
                {
                    if (_closed) return AchievementOwnerChange.Failed;
                    Guid previous = _state.OwnerId;
                    if (previous == userId) return AchievementOwnerChange.Unchanged;
                    if (_readOnly)
                    {
                        _logger.Error("Achievement state is read-only (written by a newer build); cannot switch accounts safely. Sync paused.");
                        return AchievementOwnerChange.Failed;
                    }

                    try
                    {
                        _storage.Save(AccountSlot(previous), _state);
                    }
                    catch (Exception e)
                    {
                        _logger.Error("Could not archive achievement state of the previous account; sync paused until it can be saved. " + e.Message);
                        return AchievementOwnerChange.Failed;
                    }

                    var next = LoadAccountArchive(userId) ?? new AchievementState(_catalog.GameId, _catalog.CatalogVersion, userId, _catalog.BitsetByteLength);
                    next.OwnerId = userId;
                    next.CatalogVersion = _catalog.CatalogVersion;
                    next.EnsureByteLength(_catalog.BitsetByteLength);
                    _state = next;
                    _epoch++;
                    Recount();

                    try
                    {
                        _storage.Save(ActiveSlot, _state);
                        _savedGeneration = ++_dirtyGeneration;
                        _storage.Delete(AccountSlot(userId));
                    }
                    catch (Exception e)
                    {
                        // The archive still exists, so nothing is lost; the normal save path retries.
                        _dirtyGeneration++;
                        _logger.Warning("Could not persist account switch yet: " + e.Message);
                    }

                    _logger.Info("Achievement state switched to a different account.");
                    return AchievementOwnerChange.Switched;
                }
            }
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        /// <summary>Retries a failed write once the retry delay has passed. Call periodically (e.g. from Tick).</summary>
        public void RetryFailedSave()
        {
            lock (_gate)
            {
                if (!_saveFailed || _saveRunning || _clock.MonotonicMilliseconds - _lastSaveFailureMs < SaveRetryDelayMs) return;
                _saveFailed = false;
            }
            RequestSave();
        }

        /// <summary>
        /// Synchronously writes any unsaved changes, waiting up to <paramref name="timeoutMs"/> for an
        /// in-progress background write. Use on pause/quit.
        /// </summary>
        public bool Flush(int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_gate)
            {
                while (_saveRunning)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining)) return false;
                }
                if (_dirtyGeneration == _savedGeneration) return true;
                _saveRunning = true;
            }
            SaveLoop();
            lock (_gate) return _dirtyGeneration == _savedGeneration;
        }

        private void RequestSave()
        {
            lock (_gate)
            {
                if (_saveRunning || _dirtyGeneration == _savedGeneration) return;
                _saveRunning = true;
            }

            if (_backgroundWrites)
            {
                try
                {
                    ThreadPool.QueueUserWorkItem(_ => SaveLoop());
                    return;
                }
                catch (NotSupportedException)
                {
                    // No thread pool on this platform: fall through to an inline write.
                }
            }
            SaveLoop();
        }

        private void SaveLoop()
        {
            while (true)
            {
                lock (_ioGate)
                {
                    AchievementState snapshot;
                    long generation;
                    lock (_gate)
                    {
                        if (_dirtyGeneration == _savedGeneration || _readOnly || _closed)
                        {
                            if (_readOnly) _savedGeneration = _dirtyGeneration;
                            _saveRunning = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }
                        if (_loadBlocked && !TryResolveBlockedLoad())
                        {
                            MarkSaveFailed();
                            return;
                        }
                        snapshot = _state.Clone();
                        generation = _dirtyGeneration;
                    }

                    try
                    {
                        _storage.Save(ActiveSlot, snapshot);
                        lock (_gate)
                        {
                            if (generation > _savedGeneration) _savedGeneration = generation;
                            _saveFailed = false;
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Warning("Achievement save failed; keeping state in memory and retrying later. " + e.GetType().Name + ": " + e.Message);
                        lock (_gate) MarkSaveFailed();
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Flushes, then permanently detaches this store from storage. Any operation still in flight
        /// (e.g. a sync response arriving after the game re-initialized achievements) becomes a no-op, so
        /// a superseded store can never overwrite the save file of its replacement.
        /// </summary>
        public void Close(int flushTimeoutMs = 2000)
        {
            Flush(flushTimeoutMs);
            lock (_ioGate)
            lock (_gate)
            {
                _closed = true;
            }
        }

        // Caller holds _gate.
        private void MarkSaveFailed()
        {
            _saveFailed = true;
            _lastSaveFailureMs = _clock.MonotonicMilliseconds;
            _saveRunning = false;
            Monitor.PulseAll(_gate);
        }

        // Caller holds _ioGate and _gate. Re-reads a slot that was unreadable at startup and merges the
        // in-memory session into it, so a transient lock never causes the stored state to be overwritten.
        private bool TryResolveBlockedLoad()
        {
            var result = _storage.Load(ActiveSlot);
            switch (result.Status)
            {
                case AchievementStorageLoadStatus.IoError:
                    return false;
                case AchievementStorageLoadStatus.UnsupportedVersion:
                    _readOnly = true;
                    _loadBlocked = false;
                    return false;
                case AchievementStorageLoadStatus.Loaded:
                case AchievementStorageLoadStatus.RecoveredFromBackup:
                    if (result.State.GameId == _catalog.GameId)
                    {
                        var stored = result.State;
                        stored.EnsureByteLength(Math.Max(_state.ByteLength, _catalog.BitsetByteLength));
                        for (int bit = 0; bit < _state.BitCapacity; bit++)
                        {
                            if (_state.IsUnlocked(bit)) stored.SetUnlocked(bit);
                            if (_state.IsPending(bit)) stored.SetPending(bit);
                        }
                        if (stored.OwnerId == Guid.Empty) stored.OwnerId = _state.OwnerId;
                        stored.CatalogVersion = _catalog.CatalogVersion;
                        _state = stored;
                        Recount();
                    }
                    break;
            }
            _loadBlocked = false;
            return true;
        }

        private void LoadInitial()
        {
            var result = _storage.Load(ActiveSlot);
            AchievementState state = null;

            switch (result.Status)
            {
                case AchievementStorageLoadStatus.Loaded:
                    state = result.State;
                    break;
                case AchievementStorageLoadStatus.RecoveredFromBackup:
                    _logger.Warning("Achievement save was damaged; restored the previous valid copy (" + result.Detail + ").");
                    state = result.State;
                    break;
                case AchievementStorageLoadStatus.Corrupt:
                    _logger.Error("Achievement save was corrupt and has been quarantined; starting fresh. Server reconciliation will restore synced unlocks.");
                    break;
                case AchievementStorageLoadStatus.UnsupportedVersion:
                    _logger.Error("Achievement save was written by a newer build (" + result.Detail + "); this session will not overwrite it.");
                    _readOnly = true;
                    break;
                case AchievementStorageLoadStatus.IoError:
                    _logger.Warning("Achievement save could not be read (" + result.Detail + "); will retry before writing.");
                    _loadBlocked = true;
                    break;
            }

            if (state != null && state.GameId != _catalog.GameId)
            {
                _logger.Error("Achievement save belongs to game " + state.GameId + ", not " + _catalog.GameId + "; ignoring it and preserving it on disk.");
                _readOnly = true;
                state = null;
            }

            _state = state ?? new AchievementState(_catalog.GameId, _catalog.CatalogVersion, Guid.Empty, _catalog.BitsetByteLength);
            _state.EnsureByteLength(_catalog.BitsetByteLength);
            if (_state.CatalogVersion != _catalog.CatalogVersion)
            {
                _state.CatalogVersion = _catalog.CatalogVersion;
                _dirtyGeneration++;
            }
            Recount();
        }

        private AchievementState LoadAccountArchive(Guid userId)
        {
            var result = _storage.Load(AccountSlot(userId));
            if (result.HasState && result.State.GameId == _catalog.GameId) return result.State;
            if (result.Status == AchievementStorageLoadStatus.UnsupportedVersion || result.Status == AchievementStorageLoadStatus.IoError)
                _logger.Warning("Archived achievement state for this account could not be used (" + result.Status + "); starting that account fresh.");
            return null;
        }

        private void Recount()
        {
            _unlockedCount = _state.CountUnlocked();
            // Only pending bits this build can map to an id count, so a newer build's leftovers do not
            // keep the sync scheduler busy.
            int pending = 0;
            int capacity = _state.BitCapacity;
            for (int bit = 0; bit < capacity; bit++)
                if (_state.IsPending(bit) && _catalog.TryGetByBit(bit, out _)) pending++;
            _pendingCount = pending;
        }

        private static string AccountSlot(Guid userId) => "account-" + userId.ToString("N");
    }

    public enum AchievementOwnerChange
    {
        Unchanged,

        /// <summary>A never-claimed (guest) state was adopted by the account.</summary>
        Claimed,

        /// <summary>A different account's state was archived and this account's state activated.</summary>
        Switched,

        /// <summary>The switch could not be done safely; do not sync with this account yet.</summary>
        Failed,
    }
}

