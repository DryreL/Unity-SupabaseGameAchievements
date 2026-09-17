using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class PendingSyncTests
    {
        private static string Key(int bit) => TestCatalogs.KeyForBit(bit);

        private static long Id(int bit) => TestCatalogs.IdForBit(bit);

        [Test]
        public void Signed_out_unlocks_queue_locally_and_send_nothing()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(0));
                h.System.TryUnlock(Key(1));
                h.Run(60_000, 500);

                Assert.AreEqual(2, h.System.PendingSyncCount);
                Assert.AreEqual(0, h.Api.SyncCalls.Count);
                Assert.AreEqual(0, h.Api.FetchCalls.Count);
                Assert.AreEqual(0, h.Auth.TokenRequests, "no auth lookups while signed out");
            }
        }

        [Test]
        public void Burst_of_unlocks_is_debounced_into_one_request()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(200); // startup reconciliation
                int fetches = h.Api.FetchCalls.Count;

                h.System.TryUnlock(Key(0));
                h.Run(300);
                h.System.TryUnlock(Key(1));
                h.Run(300);
                h.System.TryUnlock(Key(2));
                h.Run(900);
                Assert.AreEqual(0, h.Api.SyncCalls.Count, "still inside the debounce window");

                h.Run(200);
                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                CollectionAssert.AreEquivalent(new[] { Id(0), Id(1), Id(2) }, h.Api.SyncCalls[0].Ids);
                Assert.AreEqual(0, h.System.PendingSyncCount);
                Assert.AreEqual(3, h.Api.RowCount(FakeAuth.UserA));

                h.Run(30_000, 1000);
                Assert.AreEqual(1, h.Api.SyncCalls.Count, "no polling after success");
                Assert.AreEqual(fetches, h.Api.FetchCalls.Count, "reconciliation happens once");
            }
        }

        [Test]
        public void Reaching_the_threshold_syncs_without_waiting_for_the_debounce()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                for (int bit = 0; bit < 5; bit++) h.System.TryUnlock(Key(bit));
                h.Run(100);
                Assert.AreEqual(5, h.Api.SyncCalls.Sum(c => c.Ids.Length), "sent within 100 ms, not after the 1 s debounce");
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Large_queues_are_split_into_bounded_batches_in_one_cycle()
        {
            using (var h = new Harness())
            {
                for (int bit = 0; bit < 10; bit++) h.System.TryUnlock(Key(bit));
                h.Auth.Authenticated = true;
                h.Run(100);

                Assert.AreEqual(3, h.Api.SyncCalls.Count, "4 + 4 + 2");
                Assert.IsTrue(h.Api.SyncCalls.All(c => c.Ids.Length <= 4));
                Assert.AreEqual(10, h.Api.SyncCalls.SelectMany(c => c.Ids).Distinct().Count());
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Transient_failure_keeps_pending_and_backs_off()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.Api.ScriptedSyncOutcomes.Enqueue(AchievementApiOutcome.TransientFailure);
                h.Run(1100);
                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                Assert.AreEqual(1, h.System.PendingSyncCount, "never deleted on transient error");

                h.Run(1000);
                Assert.AreEqual(1, h.Api.SyncCalls.Count, "backing off");
                h.Run(2000);
                Assert.AreEqual(2, h.Api.SyncCalls.Count, "retried after backoff");
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Lost_response_retry_is_idempotent_and_never_renotifies()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.System.TryUnlock(Key(1));
                int toasts = h.NotificationsQueued;

                h.Api.LoseResponseAfterCommit = true; // server stored the rows, client saw a timeout
                h.Run(1100);
                Assert.AreEqual(2, h.Api.RowCount(FakeAuth.UserA));
                Assert.AreEqual(2, h.System.PendingSyncCount);

                h.Run(4000);
                Assert.AreEqual(2, h.Api.SyncCalls.Count);
                CollectionAssert.AreEquivalent(h.Api.SyncCalls[0].Ids, h.Api.SyncCalls[1].Ids);
                Assert.AreEqual(2, h.Api.RowCount(FakeAuth.UserA), "no duplicates");
                Assert.AreEqual(0, h.System.PendingSyncCount);
                Assert.AreEqual(toasts, h.NotificationsQueued);
                Assert.AreEqual(2, h.Unlocked.Count);
            }
        }

        [Test]
        public void Unlocks_during_an_in_flight_request_are_kept_for_the_next_batch()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.System.TryUnlock(Key(1));

                h.Api.SyncGate = new TaskCompletionSource<bool>();
                h.Run(1100);
                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                Assert.IsTrue(h.System.IsSyncing);

                Assert.IsTrue(h.System.TryUnlock(Key(2)), "gameplay is not blocked by the request");
                h.Run(2000);
                Assert.AreEqual(1, h.Api.SyncCalls.Count, "single request in flight");

                var gate = h.Api.SyncGate;
                h.Api.SyncGate = null;
                gate.SetResult(true);
                h.WaitIdle();

                // The in-flight cycle loops: first batch cleared, then the new unlock goes in a second request.
                Assert.AreEqual(2, h.Api.SyncCalls.Count);
                CollectionAssert.AreEquivalent(new[] { Id(0), Id(1) }, h.Api.SyncCalls[0].Ids);
                CollectionAssert.AreEqual(new[] { Id(2) }, h.Api.SyncCalls[1].Ids);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void In_flight_failure_does_not_clear_unlocks_added_meanwhile()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.Api.SyncGate = new TaskCompletionSource<bool>();
                h.Api.ScriptedSyncOutcomes.Enqueue(AchievementApiOutcome.TransientFailure);
                h.Run(1100);

                h.System.TryUnlock(Key(1));
                var gate = h.Api.SyncGate;
                h.Api.SyncGate = null;
                gate.SetResult(true);
                h.WaitIdle();

                Assert.IsTrue(h.System.IsPendingSync(Key(0)));
                Assert.IsTrue(h.System.IsPendingSync(Key(1)));
            }
        }

        [Test]
        public void Obsolete_ids_rejected_by_the_server_are_cleared_not_retried_forever()
        {
            using (var h = new Harness())
            {
                h.Api.ValidIds.Remove(Id(3)); // retired/removed on the server after this build shipped
                h.System.TryUnlock(Key(2));
                h.System.TryUnlock(Key(3));
                h.Auth.Authenticated = true;
                h.Run(1200);

                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                Assert.AreEqual(0, h.System.PendingSyncCount);
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserA));
                Assert.IsTrue(h.System.HasUnlocked(Key(3)), "local unlock is kept");
                Assert.AreEqual(1, h.Logger.Warnings.Count(w => w.Contains(Id(3).ToString())));

                h.Run(60_000, 1000);
                Assert.AreEqual(1, h.Api.SyncCalls.Count);
            }
        }

        [Test]
        public void Unknown_game_pauses_sync_and_keeps_pending()
        {
            var catalog = new AchievementCatalog(999, "wrong-game", 1, TestCatalogs.Create(3).Achievements);
            using (var h = new Harness(catalog))
            {
                h.System.TryUnlock(Key(0));
                h.Auth.Authenticated = true;
                h.Run(1200);
                int calls = h.Api.SyncCalls.Count + h.Api.FetchCalls.Count;

                h.Run(10 * 60_000, 5000);
                Assert.AreEqual(calls, h.Api.SyncCalls.Count + h.Api.FetchCalls.Count, "no retry storm");
                Assert.AreEqual(1, h.System.PendingSyncCount);
                Assert.IsTrue(h.Logger.Errors.Any(e => e.Contains("does not recognize game id 999")));
            }
        }

        [Test]
        public void Explicit_SyncAsync_ignores_the_debounce()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.System.TryUnlock(Key(0));
                var result = h.System.SyncAsync().Result;

                Assert.AreEqual(AchievementSyncStatus.Completed, result.Status);
                Assert.AreEqual(1, result.Cleared);
                Assert.AreEqual(1, h.Api.SyncCalls.Count);
            }
        }

        [Test]
        public void Explicit_SyncAsync_reports_signed_out()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(0));
                Assert.AreEqual(AchievementSyncStatus.NotAuthenticated, h.System.SyncAsync().Result.Status);
                Assert.AreEqual(1, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Network_recovery_skips_the_remaining_backoff()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                for (int i = 0; i < 4; i++) h.Api.ScriptedSyncOutcomes.Enqueue(AchievementApiOutcome.TransientFailure);
                h.Run(20_000, 100);
                int calls = h.Api.SyncCalls.Count;
                h.Api.ScriptedSyncOutcomes.Clear();

                h.System.NotifyNetworkAvailable();
                h.Run(100);
                Assert.AreEqual(calls + 1, h.Api.SyncCalls.Count);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Pausing_flushes_and_syncs_immediately()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.System.OnApplicationPausing();
                Assert.AreEqual(1, h.Api.SyncCalls.Count, "does not wait for the debounce");
            }
        }

        [Test]
        public void Reinitializing_during_an_in_flight_sync_never_overwrites_the_new_save()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Run(100);
                h.System.TryUnlock(Key(0));
                h.Api.SyncGate = new TaskCompletionSource<bool>();
                h.Run(1100);
                Assert.IsTrue(h.System.IsSyncing);
                var oldSystem = h.System;

                h.Restart(); // game re-initializes achievements while the old request is still running
                h.System.TryUnlock(Key(1));

                var gate = h.Api.SyncGate;
                h.Api.SyncGate = null;
                gate.SetResult(true);
                SpinWaitUntil(() => !oldSystem.IsSyncing);

                var saved = h.Storage.Read(AchievementStore.ActiveSlot);
                Assert.IsTrue(saved.IsUnlocked(0));
                Assert.IsTrue(saved.IsUnlocked(1), "the superseded store did not write its stale snapshot");
                Assert.IsTrue(h.System.IsPendingSync(Key(0)), "new system still owns the pending bit; the server insert is idempotent");
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserA), "the old request still reached the server");
            }
        }

        private static void SpinWaitUntil(Func<bool> condition)
        {
            if (!System.Threading.SpinWait.SpinUntil(condition, 5000)) throw new TimeoutException();
        }

        [Test]
        public void Stale_epoch_results_never_touch_another_accounts_state()
        {
            var catalog = TestCatalogs.Create(4);
            var storage = new MemoryStorage();
            var store = new AchievementStore(catalog, storage, backgroundWrites: false);
            store.TryUnlock(1);
            store.EnsureOwner(FakeAuth.UserA);
            store.SnapshotPending(new List<long>(), 10, out int epochA);

            Assert.AreEqual(AchievementOwnerChange.Switched, store.EnsureOwner(FakeAuth.UserB));
            store.TryUnlock(1);

            Assert.AreEqual(0, store.ClearPending(epochA, new[] { catalog.Achievements[1].Id }));
            Assert.AreEqual(0, store.MergeServerUnlocks(epochA, new[] { catalog.Achievements[2].Id }, null));
            Assert.IsTrue(store.IsPending(1));
            Assert.IsFalse(store.IsUnlocked(2));
        }
    }

    public class AuthTransitionTests
    {
        private static string Key(int bit) => TestCatalogs.KeyForBit(bit);

        private static long Id(int bit) => TestCatalogs.IdForBit(bit);

        [Test]
        public void Offline_unlocks_upload_in_one_request_after_login_without_popping_again()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(0));
                h.System.TryUnlock(Key(1));
                h.System.TryUnlock(Key(2));
                h.System.Notifications.Queue.Clear(); // the overlay already showed them while offline
                h.Run(10_000, 1000);

                h.Auth.SignIn();
                h.Run(200);

                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                CollectionAssert.AreEquivalent(new[] { Id(0), Id(1), Id(2) }, h.Api.SyncCalls[0].Ids);
                Assert.AreEqual(0, h.System.PendingSyncCount);
                Assert.AreEqual(0, h.NotificationsQueued);
                Assert.AreEqual(3, h.Unlocked.Count);
                Assert.AreEqual(FakeAuth.UserA, h.System.Store.OwnerId, "guest progress adopted by the account");
            }
        }

        [Test]
        public void Expired_token_is_refreshed_once_and_the_batch_retried()
        {
            using (var h = new Harness())
            {
                h.Api.TokenIsValid = token => token != "token-0";
                h.System.TryUnlock(Key(0));
                h.Auth.Authenticated = true;
                h.Run(1200);

                Assert.AreEqual(1, h.Auth.ForcedRefreshes);
                Assert.AreEqual(2, h.Api.SyncCalls.Count);
                Assert.AreEqual("token-1", h.Api.SyncCalls[1].Token);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Rejected_refresh_keeps_pending_and_waits()
        {
            using (var h = new Harness())
            {
                h.Api.TokenIsValid = token => false;
                h.System.TryUnlock(Key(0));
                h.Auth.Authenticated = true;
                h.Run(1200);

                Assert.AreEqual(1, h.Auth.ForcedRefreshes, "exactly one refresh attempt per cycle");
                Assert.AreEqual(1, h.System.PendingSyncCount);
                int calls = h.Api.SyncCalls.Count;
                h.Run(10_000, 500);
                Assert.AreEqual(calls, h.Api.SyncCalls.Count, "auth retry delay respected");

                h.Api.TokenIsValid = token => true;
                h.Run(25_000, 1000);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Auth_temporarily_unavailable_sends_nothing_and_keeps_pending()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                h.Auth.TokenUnavailable = true;
                h.System.TryUnlock(Key(0));
                h.Run(5000);

                Assert.AreEqual(0, h.Api.SyncCalls.Count);
                Assert.AreEqual(1, h.System.PendingSyncCount);
                Assert.AreEqual(Guid.Empty, h.System.Store.OwnerId);

                h.Auth.TokenUnavailable = false;
                h.Run(31_000, 1000);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Switching_accounts_never_mixes_progress()
        {
            using (var h = new Harness())
            {
                h.Auth.SignIn(FakeAuth.UserA);
                h.System.TryUnlock(Key(0));
                h.Run(1200);
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserA));

                h.Auth.SignOut();
                h.System.TryUnlock(Key(1)); // signed out: still belongs to A (sticky owner)
                h.Auth.SignIn(FakeAuth.UserB);
                h.Run(200);

                Assert.AreEqual(0, h.Api.RowCount(FakeAuth.UserB), "A's pending unlock is not uploaded as B");
                Assert.IsFalse(h.System.HasUnlocked(Key(0)), "B starts from B's own state");
                Assert.IsTrue(h.System.TryUnlock(Key(0)), "B can earn it for themselves");
                h.Run(1200);
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserB));

                h.Auth.SignIn(FakeAuth.UserA);
                h.Run(200);
                Assert.IsTrue(h.System.HasUnlocked(Key(0)));
                Assert.IsTrue(h.System.HasUnlocked(Key(1)));
                Assert.AreEqual(2, h.Api.RowCount(FakeAuth.UserA), "A's offline unlock synced once A returned");
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserB));
            }
        }
    }

    public class ReconciliationTests
    {
        private static string Key(int bit) => TestCatalogs.KeyForBit(bit);

        private static long Id(int bit) => TestCatalogs.IdForBit(bit);

        [Test]
        public void Server_unlocks_from_another_device_merge_silently()
        {
            using (var h = new Harness())
            {
                h.Api.ServerRows[FakeAuth.UserA] = new HashSet<long> { Id(5), Id(6), 999_999 };
                IReadOnlyList<AchievementDefinition> merged = null;
                h.System.ServerStateMerged += m => merged = m;

                h.Auth.Authenticated = true;
                h.Run(100);

                Assert.IsTrue(h.System.HasUnlocked(Key(5)));
                Assert.IsTrue(h.System.HasUnlocked(Key(6)));
                Assert.AreEqual(2, merged.Count, "unknown server id ignored by this build");
                Assert.AreEqual(0, h.Unlocked.Count, "no unlock events");
                Assert.AreEqual(0, h.NotificationsQueued, "no notifications");
                Assert.AreEqual(0, h.System.PendingSyncCount, "nothing to upload back");
                Assert.IsFalse(h.System.TryUnlock(Key(5)));
            }
        }

        [Test]
        public void Reconciliation_never_regresses_local_unlocks()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(1));
                h.Api.ServerRows[FakeAuth.UserA] = new HashSet<long> { Id(2) };
                h.Auth.Authenticated = true;
                var result = h.System.SyncServerStateAsync().Result;

                Assert.AreEqual(1, result.Merged);
                Assert.IsTrue(h.System.HasUnlocked(Key(1)));
                Assert.IsTrue(h.System.IsPendingSync(Key(1)));
                Assert.IsTrue(h.System.HasUnlocked(Key(2)));
            }
        }

        [Test]
        public void Local_unlock_during_reconciliation_wins_and_stays_pending()
        {
            using (var h = new Harness())
            {
                h.Api.ServerRows[FakeAuth.UserA] = new HashSet<long> { Id(4) };
                h.Api.FetchGate = new TaskCompletionSource<bool>();
                h.Auth.Authenticated = true;
                h.Run(100);
                Assert.AreEqual(1, h.Api.FetchCalls.Count);

                Assert.IsTrue(h.System.TryUnlock(Key(9)));
                Assert.AreEqual(1, h.NotificationsQueued, "UI updates immediately");

                var gate = h.Api.FetchGate;
                h.Api.FetchGate = null;
                gate.SetResult(true);
                h.WaitIdle();

                Assert.IsTrue(h.System.HasUnlocked(Key(9)));
                Assert.IsTrue(h.System.IsPendingSync(Key(9)));
                Assert.IsTrue(h.System.HasUnlocked(Key(4)));
                Assert.AreEqual(1, h.NotificationsQueued);

                h.Run(1200);
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Server_known_pending_bits_are_cleared_by_reconciliation()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(3));
                h.Api.ServerRows[FakeAuth.UserA] = new HashSet<long> { Id(3) };
                h.Auth.Authenticated = true;
                h.System.SyncServerStateAsync().Wait();

                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Failed_reconciliation_is_retried_with_backoff()
        {
            using (var h = new Harness())
            {
                h.Api.ScriptedFetchOutcomes.Enqueue(AchievementApiOutcome.TransientFailure);
                h.Api.ServerRows[FakeAuth.UserA] = new HashSet<long> { Id(1) };
                h.Auth.Authenticated = true;
                h.Run(100);
                Assert.IsFalse(h.System.HasUnlocked(Key(1)));

                h.Run(5000);
                Assert.AreEqual(2, h.Api.FetchCalls.Count);
                Assert.IsTrue(h.System.HasUnlocked(Key(1)));
            }
        }
    }
}

