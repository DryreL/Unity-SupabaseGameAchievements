using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements
{
    public sealed class AchievementSyncOptions
    {
        /// <summary>Quiet period after the last unlock before uploading.</summary>
        public int DebounceMilliseconds { get; set; } = 1000;

        /// <summary>Upload immediately (skipping the debounce) once this many unlocks are pending.</summary>
        public int ImmediateSyncThreshold { get; set; } = 12;

        /// <summary>Ids per request. The server accepts at most 256.</summary>
        public int MaxBatchSize { get; set; } = 100;

        public int MinRetryDelayMilliseconds { get; set; } = 2000;

        public int MaxRetryDelayMilliseconds { get; set; } = 5 * 60 * 1000;

        /// <summary>Wait after a token could not be obtained or was rejected twice.</summary>
        public int AuthRetryDelayMilliseconds { get; set; } = 30 * 1000;

        /// <summary>Wait after a non-transient server rejection (e.g. backend not deployed).</summary>
        public int PermanentFailureRetryDelayMilliseconds { get; set; } = 10 * 60 * 1000;
    }

    public enum AchievementSyncStatus
    {
        Completed,
        NotAuthenticated,
        AuthUnavailable,
        Failed,

        /// <summary>The server does not know this game. Synchronization is paused for the session; pending data is kept.</summary>
        Halted,

        /// <summary>No backend configured; achievements are local-only.</summary>
        Disabled,
    }

    public readonly struct AchievementSyncResult
    {
        public AchievementSyncResult(AchievementSyncStatus status, int cleared = 0, int rejected = 0, int merged = 0, string error = null)
        {
            Status = status;
            Cleared = cleared;
            Rejected = rejected;
            Merged = merged;
            Error = error;
        }

        public AchievementSyncStatus Status { get; }

        /// <summary>Pending unlocks confirmed by the server and removed from the queue.</summary>
        public int Cleared { get; }

        /// <summary>Pending unlocks the server permanently rejected (obsolete/invalid) and removed from the queue.</summary>
        public int Rejected { get; }

        /// <summary>Unlocks learned from the server during reconciliation.</summary>
        public int Merged { get; }

        public string Error { get; }
    }

    /// <summary>
    /// Moves pending unlocks to the server with at-least-once semantics (the server is idempotent) and
    /// merges server-side unlocks back. Driven by <see cref="Tick"/> from the game loop: no timers, no
    /// polling requests, at most one request in flight, never awaited by gameplay.
    /// </summary>
    public sealed class AchievementSyncService : IDisposable
    {
        private readonly AchievementCatalog _catalog;
        private readonly AchievementStore _store;
        private readonly IAchievementAuthProvider _auth;
        private readonly IAchievementApiClient _api;
        private readonly IAchievementClock _clock;
        private readonly IAchievementLogger _logger;
        private readonly IAchievementDispatcher _dispatcher;
        private readonly AchievementSyncOptions _options;
        private readonly SemaphoreSlim _cycleGate = new SemaphoreSlim(1, 1);
        private readonly Random _jitter = new Random();

        // Reused by the single in-flight cycle.
        private readonly List<long> _batch = new List<long>();
        private readonly HashSet<long> _batchSet = new HashSet<long>();
        private readonly List<long> _confirmed = new List<long>();
        private readonly List<AchievementDefinition> _merged = new List<AchievementDefinition>();
        private readonly HashSet<long> _reportedRejections = new HashSet<long>();

        private long _lastUnlockMs;
        private long _nextUploadMs;
        private long _nextReconcileMs;
        private int _uploadFailures;
        private int _reconcileFailures;
        private int _reconcileNeeded = 1;
        private int _immediateRequested;
        private int _authChanged;
        private bool _wasAuthenticated;
        private volatile bool _halted;
        private volatile bool _disposed;

        public AchievementSyncService(
            AchievementCatalog catalog,
            AchievementStore store,
            IAchievementAuthProvider auth,
            IAchievementApiClient api,
            IAchievementClock clock = null,
            IAchievementLogger logger = null,
            IAchievementDispatcher dispatcher = null,
            AchievementSyncOptions options = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _clock = clock ?? SystemAchievementClock.Instance;
            _logger = logger ?? NullAchievementLogger.Instance;
            _dispatcher = dispatcher ?? InlineAchievementDispatcher.Instance;
            _options = options ?? new AchievementSyncOptions();
            _options.MaxBatchSize = Math.Max(1, Math.Min(_options.MaxBatchSize, 256));
            _auth.AuthenticationStateChanged += OnAuthenticationStateChanged;
        }

        /// <summary>Raised (via the dispatcher) with achievements learned from the server. Never a notification trigger.</summary>
        public event Action<IReadOnlyList<AchievementDefinition>> ServerStateMerged;

        /// <summary>Raised (via the dispatcher) when a different account's local state was activated.</summary>
        public event Action AccountChanged;

        public bool IsSyncing => _cycleGate.CurrentCount == 0;

        public bool IsHalted => _halted;

        /// <summary>Call after every successful local unlock; restarts the debounce window.</summary>
        public void NotifyUnlocked() => Interlocked.Exchange(ref _lastUnlockMs, _clock.MonotonicMilliseconds);

        /// <summary>Connectivity came back: retry now instead of waiting out the backoff.</summary>
        public void NotifyNetworkAvailable()
        {
            Interlocked.Exchange(ref _nextUploadMs, 0);
            Interlocked.Exchange(ref _nextReconcileMs, 0);
            Interlocked.Exchange(ref _immediateRequested, 1);
        }

        /// <summary>Skip the debounce on the next opportunity (pause/quit).</summary>
        public void RequestImmediateSync() => Interlocked.Exchange(ref _immediateRequested, 1);

        /// <summary>Cheap scheduler step. Call once per frame (or less often) from the owning loop.</summary>
        public void Tick()
        {
            if (_disposed) return;

            if (Interlocked.Exchange(ref _authChanged, 0) == 1)
            {
                Interlocked.Exchange(ref _reconcileNeeded, 1);
                NotifyNetworkAvailable();
            }

            bool authenticated = _auth.IsAuthenticated;
            if (authenticated && !_wasAuthenticated)
            {
                Interlocked.Exchange(ref _reconcileNeeded, 1);
                NotifyNetworkAvailable();
            }
            _wasAuthenticated = authenticated;
            if (!authenticated || _halted || IsSyncing) return;

            long now = _clock.MonotonicMilliseconds;
            int pending = _store.PendingCount;
            bool upload = pending > 0
                && now >= Interlocked.Read(ref _nextUploadMs)
                && (Volatile.Read(ref _immediateRequested) == 1
                    || pending >= _options.ImmediateSyncThreshold
                    || now - Interlocked.Read(ref _lastUnlockMs) >= _options.DebounceMilliseconds);
            bool reconcile = Volatile.Read(ref _reconcileNeeded) == 1 && now >= Interlocked.Read(ref _nextReconcileMs);
            if (!upload && !reconcile) return;
            if (!_cycleGate.Wait(0)) return;

            Interlocked.Exchange(ref _immediateRequested, 0);
            _ = RunOwnedCycleAsync(upload, reconcile);
        }

        /// <summary>Uploads all pending unlocks now, ignoring debounce and backoff.</summary>
        public Task<AchievementSyncResult> SyncAsync(CancellationToken cancellationToken = default) => RunExplicitAsync(true, false, cancellationToken);

        /// <summary>Fetches the server's unlocks for this game and merges them into local state.</summary>
        public Task<AchievementSyncResult> SyncServerStateAsync(CancellationToken cancellationToken = default) => RunExplicitAsync(false, true, cancellationToken);

        /// <summary>
        /// Stops scheduling. A request already in flight is allowed to finish (best effort on quit); its
        /// result is discarded by the closed store and the server insert is idempotent anyway.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _auth.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

        private void OnAuthenticationStateChanged() => Interlocked.Exchange(ref _authChanged, 1);

        private async Task RunOwnedCycleAsync(bool upload, bool reconcile)
        {
            try
            {
                await RunCycleAsync(upload, reconcile, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Shutting down; pending state is already on disk.
            }
            catch (Exception e)
            {
                _logger.Error("Achievement sync cycle failed unexpectedly: " + e);
                ScheduleUploadRetry();
            }
            finally
            {
                _cycleGate.Release();
            }
        }

        private async Task<AchievementSyncResult> RunExplicitAsync(bool upload, bool reconcile, CancellationToken cancellationToken)
        {
            if (!_auth.IsAuthenticated) return new AchievementSyncResult(AchievementSyncStatus.NotAuthenticated);
            if (_disposed) return new AchievementSyncResult(AchievementSyncStatus.Disabled);
            await _cycleGate.WaitAsync(cancellationToken);
            try
            {
                return await RunCycleAsync(upload, reconcile, cancellationToken);
            }
            finally
            {
                _cycleGate.Release();
            }
        }

        // Caller holds _cycleGate.
        private async Task<AchievementSyncResult> RunCycleAsync(bool upload, bool reconcile, CancellationToken ct)
        {
            if (_halted) return new AchievementSyncResult(AchievementSyncStatus.Halted);

            var session = new CycleSession();
            var status = await AcquireTokenAsync(session, false, ct);
            if (status != AchievementSyncStatus.Completed)
            {
                Interlocked.Exchange(ref _nextUploadMs, _clock.MonotonicMilliseconds + _options.AuthRetryDelayMilliseconds);
                Interlocked.Exchange(ref _nextReconcileMs, _clock.MonotonicMilliseconds + _options.AuthRetryDelayMilliseconds);
                return new AchievementSyncResult(status);
            }

            var result = new AchievementSyncResult(AchievementSyncStatus.Completed);
            if (upload)
            {
                result = await UploadAsync(session, ct);
                if (result.Status == AchievementSyncStatus.Halted) return result;
            }

            // An account claim/switch during an upload-only cycle sets _reconcileNeeded; Tick picks it up next.
            if (reconcile)
            {
                var reconciled = await ReconcileAsync(session, ct);
                result = new AchievementSyncResult(
                    result.Status == AchievementSyncStatus.Completed ? reconciled.Status : result.Status,
                    result.Cleared, result.Rejected, reconciled.Merged, result.Error ?? reconciled.Error);
            }
            return result;
        }

        private async Task<AchievementSyncResult> UploadAsync(CycleSession session, CancellationToken ct)
        {
            int cleared = 0;
            int rejected = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_store.SnapshotPending(_batch, _options.MaxBatchSize, out int epoch) == 0) break;

                var response = await _api.SyncAsync(session.Token.AccessToken, _catalog.GameId, _batch, ct);
                switch (response.Outcome)
                {
                    case AchievementApiOutcome.Success:
                        _batchSet.Clear();
                        foreach (long id in _batch) _batchSet.Add(id);
                        _confirmed.Clear();
                        foreach (long id in response.Accepted)
                            if (_batchSet.Contains(id)) _confirmed.Add(id);
                        foreach (long id in response.Rejected)
                        {
                            if (!_batchSet.Contains(id)) continue;
                            _confirmed.Add(id);
                            rejected++;
                            if (_reportedRejections.Add(id))
                                _logger.Warning("Server rejected achievement id " + id + DescribeId(id) + " as unknown, retired, or belonging to another game. It was removed from the sync queue.");
                        }

                        int removed = _store.ClearPending(epoch, _confirmed);
                        cleared += removed;
                        if (removed == 0 && epoch == _store.Epoch)
                        {
                            // The server confirmed nothing we sent. Avoid a hot loop; try again later.
                            ScheduleUploadRetry();
                            return new AchievementSyncResult(AchievementSyncStatus.Failed, cleared, rejected, error: "Server confirmed none of the submitted achievements.");
                        }
                        _uploadFailures = 0;
                        continue;

                    case AchievementApiOutcome.Unauthorized:
                        if (session.Refreshed)
                        {
                            Interlocked.Exchange(ref _nextUploadMs, _clock.MonotonicMilliseconds + _options.AuthRetryDelayMilliseconds);
                            return new AchievementSyncResult(AchievementSyncStatus.AuthUnavailable, cleared, rejected, error: response.Error);
                        }
                        var status = await AcquireTokenAsync(session, true, ct);
                        if (status != AchievementSyncStatus.Completed)
                        {
                            Interlocked.Exchange(ref _nextUploadMs, _clock.MonotonicMilliseconds + _options.AuthRetryDelayMilliseconds);
                            return new AchievementSyncResult(status, cleared, rejected, error: response.Error);
                        }
                        continue;

                    case AchievementApiOutcome.UnknownGame:
                        Halt(response.Error);
                        return new AchievementSyncResult(AchievementSyncStatus.Halted, cleared, rejected, error: response.Error);

                    case AchievementApiOutcome.PermanentFailure:
                        _logger.Warning("Achievement sync rejected by the server; will retry much later. " + response.Error);
                        Interlocked.Exchange(ref _nextUploadMs, _clock.MonotonicMilliseconds + _options.PermanentFailureRetryDelayMilliseconds);
                        return new AchievementSyncResult(AchievementSyncStatus.Failed, cleared, rejected, error: response.Error);

                    default:
                        ScheduleUploadRetry();
                        return new AchievementSyncResult(AchievementSyncStatus.Failed, cleared, rejected, error: response.Error);
                }
            }

            _uploadFailures = 0;
            Interlocked.Exchange(ref _nextUploadMs, 0);
            return new AchievementSyncResult(AchievementSyncStatus.Completed, cleared, rejected);
        }

        private async Task<AchievementSyncResult> ReconcileAsync(CycleSession session, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int epoch = _store.Epoch;
                var response = await _api.GetUnlockedAsync(session.Token.AccessToken, _catalog.GameId, ct);
                switch (response.Outcome)
                {
                    case AchievementApiOutcome.Success:
                        _merged.Clear();
                        _store.MergeServerUnlocks(epoch, response.UnlockedIds, _merged);
                        if (epoch != _store.Epoch)
                        {
                            // Account switched mid-request: the response belongs to another state.
                            Interlocked.Exchange(ref _reconcileNeeded, 1);
                            return new AchievementSyncResult(AchievementSyncStatus.Completed);
                        }
                        Interlocked.Exchange(ref _reconcileNeeded, 0);
                        _reconcileFailures = 0;
                        int mergedCount = _merged.Count;
                        if (mergedCount > 0)
                        {
                            var snapshot = _merged.ToArray();
                            Post(() => ServerStateMerged?.Invoke(snapshot));
                        }
                        return new AchievementSyncResult(AchievementSyncStatus.Completed, merged: mergedCount);

                    case AchievementApiOutcome.Unauthorized:
                        if (!session.Refreshed)
                        {
                            var status = await AcquireTokenAsync(session, true, ct);
                            if (status == AchievementSyncStatus.Completed) continue;
                        }
                        Interlocked.Exchange(ref _nextReconcileMs, _clock.MonotonicMilliseconds + _options.AuthRetryDelayMilliseconds);
                        return new AchievementSyncResult(AchievementSyncStatus.AuthUnavailable, error: response.Error);

                    case AchievementApiOutcome.UnknownGame:
                        Halt(response.Error);
                        return new AchievementSyncResult(AchievementSyncStatus.Halted, error: response.Error);

                    case AchievementApiOutcome.PermanentFailure:
                        Interlocked.Exchange(ref _nextReconcileMs, _clock.MonotonicMilliseconds + _options.PermanentFailureRetryDelayMilliseconds);
                        return new AchievementSyncResult(AchievementSyncStatus.Failed, error: response.Error);

                    default:
                        _reconcileFailures++;
                        Interlocked.Exchange(ref _nextReconcileMs, _clock.MonotonicMilliseconds + BackoffDelay(_reconcileFailures));
                        return new AchievementSyncResult(AchievementSyncStatus.Failed, error: response.Error);
                }
            }
        }

        private async Task<AchievementSyncStatus> AcquireTokenAsync(CycleSession session, bool forceRefresh, CancellationToken ct)
        {
            if (forceRefresh) session.Refreshed = true;

            AchievementAuthToken token;
            try
            {
                token = await _auth.GetAccessTokenAsync(forceRefresh, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Warning("Auth provider failed: " + e.Message);
                return AchievementSyncStatus.AuthUnavailable;
            }
            if (!token.IsValid) return AchievementSyncStatus.AuthUnavailable;

            switch (_store.EnsureOwner(token.UserId))
            {
                case AchievementOwnerChange.Failed:
                    return AchievementSyncStatus.Failed;
                case AchievementOwnerChange.Switched:
                    Interlocked.Exchange(ref _reconcileNeeded, 1);
                    Post(() => AccountChanged?.Invoke());
                    break;
                case AchievementOwnerChange.Claimed:
                    Interlocked.Exchange(ref _reconcileNeeded, 1);
                    break;
            }

            session.Token = token;
            return AchievementSyncStatus.Completed;
        }

        private void Halt(string error)
        {
            _halted = true;
            _logger.Error(
                "Achievement sync paused for this session: the server does not recognize game id " + _catalog.GameId +
                " ('" + _catalog.GameSlug + "'). The shipped achievement manifest does not match the backend. Pending unlocks are kept. " + error);
        }

        private void ScheduleUploadRetry()
        {
            _uploadFailures++;
            Interlocked.Exchange(ref _nextUploadMs, _clock.MonotonicMilliseconds + BackoffDelay(_uploadFailures));
        }

        private long BackoffDelay(int failures)
        {
            double delay = _options.MinRetryDelayMilliseconds * Math.Pow(2, Math.Min(failures - 1, 16));
            delay = Math.Min(delay, _options.MaxRetryDelayMilliseconds);
            return (long)(delay * (0.8 + _jitter.NextDouble() * 0.4));
        }

        private string DescribeId(long id) => _catalog.TryGetById(id, out var definition) ? " ('" + definition.Key + "')" : string.Empty;

        private void Post(Action action)
        {
            try
            {
                _dispatcher.Post(action);
            }
            catch (Exception e)
            {
                _logger.Error("Achievement event handler failed: " + e);
            }
        }

        private sealed class CycleSession
        {
            public AchievementAuthToken Token;
            public bool Refreshed;
        }
    }
}

