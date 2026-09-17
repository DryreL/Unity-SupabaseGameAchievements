using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    internal static class TestCatalogs
    {
        public const long GameId = 7;

        public static AchievementCatalog Create(int count = 3, int catalogVersion = 1, params int[] retiredBits)
        {
            var definitions = new List<AchievementDefinition>();
            for (int bit = 0; bit < count; bit++)
            {
                definitions.Add(new AchievementDefinition(
                    id: IdForBit(bit),
                    key: KeyForBit(bit),
                    bitIndex: bit,
                    title: "Title " + bit,
                    description: "Description " + bit,
                    iconPath: "achievements/" + KeyForBit(bit),
                    retired: retiredBits.Contains(bit)));
            }
            return new AchievementCatalog(GameId, "test-game", catalogVersion, definitions);
        }

        public static long IdForBit(int bit) => 1000 + bit;

        public static string KeyForBit(int bit) => "achievement_" + bit;
    }

    internal sealed class FakeClock : IAchievementClock
    {
        public long MonotonicMilliseconds { get; set; } = 1_000_000;

        public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        public void Advance(int milliseconds)
        {
            MonotonicMilliseconds += milliseconds;
            UtcNow = UtcNow.AddMilliseconds(milliseconds);
        }
    }

    internal sealed class ListLogger : IAchievementLogger
    {
        public readonly List<string> Infos = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public void Info(string message) { lock (Infos) Infos.Add(message); }

        public void Warning(string message) { lock (Warnings) Warnings.Add(message); }

        public void Error(string message) { lock (Errors) Errors.Add(message); }
    }

    /// <summary>In-memory storage with the same slot semantics and failure injection.</summary>
    internal sealed class MemoryStorage : IAchievementStorage
    {
        public readonly Dictionary<string, byte[]> Slots = new Dictionary<string, byte[]>();
        public int SaveCount;
        public bool FailSaves;
        public AchievementStorageLoadStatus? ForcedLoadStatus;

        public AchievementStorageLoadResult Load(string slot)
        {
            lock (Slots)
            {
                if (ForcedLoadStatus.HasValue) return new AchievementStorageLoadResult(ForcedLoadStatus.Value, null, "forced");
                if (!Slots.TryGetValue(slot, out var bytes)) return new AchievementStorageLoadResult(AchievementStorageLoadStatus.NotFound, null);
                return AchievementState.TryParse(bytes, out var state, out _) == AchievementStateReadStatus.Ok
                    ? new AchievementStorageLoadResult(AchievementStorageLoadStatus.Loaded, state)
                    : new AchievementStorageLoadResult(AchievementStorageLoadStatus.Corrupt, null);
            }
        }

        public void Save(string slot, AchievementState state)
        {
            lock (Slots)
            {
                if (FailSaves) throw new IOException("disk full");
                Slots[slot] = state.ToBytes();
                SaveCount++;
            }
        }

        public bool Exists(string slot)
        {
            lock (Slots) return Slots.ContainsKey(slot);
        }

        public void Delete(string slot)
        {
            lock (Slots) Slots.Remove(slot);
        }

        public AchievementState Read(string slot)
        {
            lock (Slots)
            {
                AchievementState.TryParse(Slots[slot], out var state, out _);
                return state;
            }
        }
    }

    internal sealed class FakeAuth : IAchievementAuthProvider
    {
        public static readonly Guid UserA = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid UserB = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        public bool Authenticated;
        public Guid UserId = UserA;
        public bool TokenUnavailable;
        public int TokenRequests;
        public int ForcedRefreshes;
        public int TokenSerial;

        public bool IsAuthenticated => Authenticated;

        public event Action AuthenticationStateChanged;

        public Task<AchievementAuthToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            TokenRequests++;
            if (forceRefresh)
            {
                ForcedRefreshes++;
                TokenSerial++;
            }
            if (!Authenticated || TokenUnavailable) return Task.FromResult(AchievementAuthToken.None);
            return Task.FromResult(new AchievementAuthToken("token-" + TokenSerial, UserId));
        }

        public void SignIn(Guid? user = null)
        {
            if (user.HasValue) UserId = user.Value;
            Authenticated = true;
            AuthenticationStateChanged?.Invoke();
        }

        public void SignOut()
        {
            Authenticated = false;
            AuthenticationStateChanged?.Invoke();
        }
    }

    /// <summary>Scriptable backend that behaves like the real SQL function by default.</summary>
    internal sealed class FakeApi : IAchievementApiClient
    {
        public readonly List<(string Token, long GameId, long[] Ids)> SyncCalls = new List<(string, long, long[])>();
        public readonly List<string> FetchCalls = new List<string>();
        public readonly Dictionary<Guid, HashSet<long>> ServerRows = new Dictionary<Guid, HashSet<long>>();
        public readonly HashSet<long> ValidIds = new HashSet<long>();
        public readonly Queue<AchievementApiOutcome> ScriptedSyncOutcomes = new Queue<AchievementApiOutcome>();
        public readonly Queue<AchievementApiOutcome> ScriptedFetchOutcomes = new Queue<AchievementApiOutcome>();
        public Func<string, bool> TokenIsValid = token => true;
        public Func<string, Guid> UserForToken;
        public TaskCompletionSource<bool> SyncGate;
        public TaskCompletionSource<bool> FetchGate;
        public bool LoseResponseAfterCommit;

        public FakeApi(AchievementCatalog catalog, Func<string, Guid> userForToken)
        {
            foreach (var definition in catalog.Achievements)
                if (!definition.IsRetired) ValidIds.Add(definition.Id);
            UserForToken = userForToken;
        }

        public int RowCount(Guid user) => ServerRows.TryGetValue(user, out var rows) ? rows.Count : 0;

        public async Task<AchievementSyncResponse> SyncAsync(string accessToken, long gameId, IReadOnlyList<long> achievementIds, CancellationToken cancellationToken)
        {
            var ids = achievementIds.ToArray();
            SyncCalls.Add((accessToken, gameId, ids));
            if (SyncGate != null) await SyncGate.Task;

            if (ScriptedSyncOutcomes.Count > 0)
            {
                var outcome = ScriptedSyncOutcomes.Dequeue();
                if (outcome != AchievementApiOutcome.Success) return new AchievementSyncResponse(outcome, error: "scripted " + outcome);
            }
            if (!TokenIsValid(accessToken)) return new AchievementSyncResponse(AchievementApiOutcome.Unauthorized, error: "expired");
            if (gameId != TestCatalogs.GameId) return new AchievementSyncResponse(AchievementApiOutcome.UnknownGame, error: "unknown_game");

            var user = UserForToken(accessToken);
            if (!ServerRows.TryGetValue(user, out var rows)) ServerRows[user] = rows = new HashSet<long>();
            var accepted = ids.Distinct().Where(ValidIds.Contains).OrderBy(x => x).ToArray();
            var rejected = ids.Distinct().Where(id => !ValidIds.Contains(id)).OrderBy(x => x).ToArray();
            int inserted = accepted.Count(rows.Add);

            if (LoseResponseAfterCommit)
            {
                LoseResponseAfterCommit = false;
                return new AchievementSyncResponse(AchievementApiOutcome.TransientFailure, error: "response lost");
            }
            return new AchievementSyncResponse(AchievementApiOutcome.Success, accepted, rejected, inserted);
        }

        public async Task<AchievementFetchResponse> GetUnlockedAsync(string accessToken, long gameId, CancellationToken cancellationToken)
        {
            FetchCalls.Add(accessToken);
            if (FetchGate != null) await FetchGate.Task;
            if (ScriptedFetchOutcomes.Count > 0)
            {
                var outcome = ScriptedFetchOutcomes.Dequeue();
                if (outcome != AchievementApiOutcome.Success) return new AchievementFetchResponse(outcome, error: "scripted " + outcome);
            }
            if (!TokenIsValid(accessToken)) return new AchievementFetchResponse(AchievementApiOutcome.Unauthorized);
            var user = UserForToken(accessToken);
            return new AchievementFetchResponse(
                AchievementApiOutcome.Success,
                ServerRows.TryGetValue(user, out var rows) ? rows.OrderBy(x => x).ToArray() : new long[0]);
        }
    }

    /// <summary>Wires a complete <see cref="AchievementSystem"/> with fakes and synchronous writes.</summary>
    internal sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly MemoryStorage Storage;
        public readonly FakeAuth Auth = new FakeAuth();
        public readonly ListLogger Logger = new ListLogger();
        public readonly FixedAchievementNotificationSettings Settings = new FixedAchievementNotificationSettings(true);
        public readonly AchievementCatalog Catalog;
        public readonly FakeApi Api;
        public AchievementSystem System;
        public readonly List<AchievementUnlockedEvent> Unlocked = new List<AchievementUnlockedEvent>();

        public Harness(AchievementCatalog catalog = null, MemoryStorage storage = null, IAchievementLocalizationProvider localization = null, bool withApi = true)
        {
            Catalog = catalog ?? TestCatalogs.Create(20);
            Storage = storage ?? new MemoryStorage();
            Api = new FakeApi(Catalog, token => Auth.UserId);
            Start(localization, withApi);
        }

        public void Start(IAchievementLocalizationProvider localization = null, bool withApi = true)
        {
            System = new AchievementSystem(new AchievementSystemOptions
            {
                Catalog = Catalog,
                Storage = Storage,
                ApiClient = withApi ? Api : null,
                AuthProvider = Auth,
                NotificationSettings = Settings,
                Localization = localization ?? DefaultAchievementLocalizationProvider.Instance,
                Logger = Logger,
                Clock = Clock,
                BackgroundWrites = false,
                Sync = new AchievementSyncOptions { DebounceMilliseconds = 1000, ImmediateSyncThreshold = 5, MaxBatchSize = 4 },
            });
            System.AchievementUnlocked += Unlocked.Add;
        }

        /// <summary>Simulates a process restart against the same storage.</summary>
        public void Restart()
        {
            System.Dispose();
            Unlocked.Clear();
            Start();
        }

        /// <summary>Advances time and ticks, like frames passing.</summary>
        public void Run(int milliseconds, int step = 100)
        {
            for (int elapsed = 0; elapsed < milliseconds; elapsed += step)
            {
                Clock.Advance(step);
                System.Tick();
            }
        }

        public int NotificationsQueued => System.Notifications.Queue.Count;

        /// <summary>Waits for an in-flight sync cycle released from another thread to finish.</summary>
        public void WaitIdle(int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (System.IsSyncing)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Sync cycle did not finish.");
                Thread.Sleep(1);
            }
        }

        public void Dispose() => System.Dispose();
    }
}

