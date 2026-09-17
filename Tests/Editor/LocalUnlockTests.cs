using System;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class LocalUnlockTests
    {
        [Test]
        public void Unknown_key_returns_false_warns_once_and_sends_nothing()
        {
            using (var h = new Harness())
            {
                h.Auth.Authenticated = true;
                Assert.IsFalse(h.System.TryUnlock("does_not_exist"));
                Assert.IsFalse(h.System.TryUnlock("does_not_exist"));
                h.Run(5000);

                Assert.AreEqual(1, h.Logger.Warnings.FindAll(w => w.Contains("does_not_exist")).Count);
                Assert.AreEqual(0, h.Unlocked.Count);
                Assert.AreEqual(0, h.System.PendingSyncCount);
                Assert.AreEqual(0, h.Api.SyncCalls.Count);
            }
        }

        [Test]
        public void New_unlock_is_immediate_raises_one_event_and_queues_sync()
        {
            using (var h = new Harness())
            {
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(3)));

                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(3)));
                Assert.IsTrue(h.System.IsPendingSync(TestCatalogs.KeyForBit(3)));
                Assert.AreEqual(1, h.Unlocked.Count);
                Assert.AreEqual(TestCatalogs.IdForBit(3), h.Unlocked[0].Definition.Id);
                Assert.AreEqual(1, h.NotificationsQueued);
                Assert.AreEqual(1, h.Storage.SaveCount, "persisted before returning (synchronous writes in tests)");
            }
        }

        [Test]
        public void Unlocking_twice_returns_false_and_has_no_side_effects()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(TestCatalogs.KeyForBit(1));
                int saves = h.Storage.SaveCount;

                Assert.IsFalse(h.System.TryUnlock(TestCatalogs.KeyForBit(1)));
                Assert.AreEqual(1, h.Unlocked.Count);
                Assert.AreEqual(1, h.NotificationsQueued);
                Assert.AreEqual(1, h.System.PendingSyncCount);
                Assert.AreEqual(saves, h.Storage.SaveCount);
            }
        }

        [Test]
        public void Retired_achievement_cannot_be_unlocked()
        {
            using (var h = new Harness(TestCatalogs.Create(4, 1, 2)))
            {
                Assert.IsFalse(h.System.TryUnlock(TestCatalogs.KeyForBit(2)));
                Assert.AreEqual(0, h.System.PendingSyncCount);
            }
        }

        [Test]
        public void Unlocks_survive_restart_without_re_notifying()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(TestCatalogs.KeyForBit(0));
                h.System.TryUnlock(TestCatalogs.KeyForBit(9));
                h.Restart();

                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(0)));
                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(9)));
                Assert.AreEqual(2, h.System.PendingSyncCount, "pending survives restart");
                Assert.IsFalse(h.System.TryUnlock(TestCatalogs.KeyForBit(0)));
                Assert.AreEqual(0, h.Unlocked.Count);
                Assert.AreEqual(0, h.NotificationsQueued);
            }
        }

        [Test]
        public void Corrupt_save_starts_fresh_without_crashing()
        {
            var storage = new MemoryStorage();
            storage.Slots[AchievementStore.ActiveSlot] = new byte[] { 1, 2, 3, 4, 5 };
            using (var h = new Harness(storage: storage))
            {
                Assert.AreEqual(0, h.System.UnlockedCount);
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(0)));
                Assert.AreEqual(1, h.Logger.Errors.Count);
            }
        }

        [Test]
        public void Disk_write_failure_keeps_state_in_memory_and_retries()
        {
            using (var h = new Harness())
            {
                h.Storage.FailSaves = true;
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(4)), "gameplay unaffected by disk failure");
                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(4)));
                Assert.IsTrue(h.System.Store.HasUnsavedChanges);

                h.Storage.FailSaves = false;
                h.Run(1000);
                Assert.IsTrue(h.System.Store.HasUnsavedChanges, "retry waits for the delay");
                h.Run(5000);
                Assert.IsFalse(h.System.Store.HasUnsavedChanges);
                Assert.IsTrue(h.Storage.Read(AchievementStore.ActiveSlot).IsUnlocked(4));
            }
        }

        [Test]
        public void Save_written_by_newer_build_is_never_overwritten()
        {
            var storage = new MemoryStorage();
            var newer = new AchievementState(TestCatalogs.GameId, 1, Guid.Empty, 3).ToBytes();
            newer[4] = 2;
            storage.Slots[AchievementStore.ActiveSlot] = newer;
            storage.ForcedLoadStatus = AchievementStorageLoadStatus.UnsupportedVersion;

            using (var h = new Harness(storage: storage))
            {
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(0)), "still works in memory");
                h.System.Store.Flush();
                CollectionAssert.AreEqual(newer, storage.Slots[AchievementStore.ActiveSlot]);
            }
        }

        [Test]
        public void Unreadable_save_is_merged_not_overwritten_once_readable()
        {
            var storage = new MemoryStorage();
            var existing = new AchievementState(TestCatalogs.GameId, 1, Guid.Empty, 3);
            existing.SetUnlocked(7);
            storage.Slots[AchievementStore.ActiveSlot] = existing.ToBytes();
            storage.ForcedLoadStatus = AchievementStorageLoadStatus.IoError; // e.g. antivirus lock at startup

            using (var h = new Harness(storage: storage))
            {
                storage.ForcedLoadStatus = null;
                h.System.TryUnlock(TestCatalogs.KeyForBit(1));

                var saved = storage.Read(AchievementStore.ActiveSlot);
                Assert.IsTrue(saved.IsUnlocked(7), "previous unlock preserved");
                Assert.IsTrue(saved.IsUnlocked(1));
                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(7)));
            }
        }

        [Test]
        public void Older_state_loads_into_a_larger_newer_catalog()
        {
            var storage = new MemoryStorage();
            using (var h = new Harness(TestCatalogs.Create(3, 1), storage))
            {
                h.System.TryUnlock(TestCatalogs.KeyForBit(2));
            }

            using (var h = new Harness(TestCatalogs.Create(40, 2), storage))
            {
                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(2)));
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(39)));
                h.System.Store.Flush();
                var saved = storage.Read(AchievementStore.ActiveSlot);
                Assert.AreEqual(2, saved.CatalogVersion);
                Assert.IsTrue(saved.IsUnlocked(39));
            }
        }

        [Test]
        public void Newer_state_keeps_unknown_bits_when_an_older_build_runs()
        {
            var storage = new MemoryStorage();
            using (var h = new Harness(TestCatalogs.Create(40, 2), storage, withApi: true))
            {
                h.System.TryUnlock(TestCatalogs.KeyForBit(39));
            }

            using (var h = new Harness(TestCatalogs.Create(3, 1), storage))
            {
                h.Auth.Authenticated = true;
                h.System.TryUnlock(TestCatalogs.KeyForBit(0));
                h.Run(3000);

                Assert.AreEqual(1, h.Api.SyncCalls.Count);
                CollectionAssert.AreEqual(new[] { TestCatalogs.IdForBit(0) }, h.Api.SyncCalls[0].Ids, "unknown bit is not sent by the old build");
                var saved = storage.Read(AchievementStore.ActiveSlot);
                Assert.IsTrue(saved.IsUnlocked(39) && saved.IsPending(39), "newer build's pending unlock preserved");
            }
        }

        [Test]
        public void Handler_exceptions_do_not_break_unlocking()
        {
            using (var h = new Harness())
            {
                h.System.AchievementUnlocked += _ => throw new InvalidOperationException("boom");
                Assert.IsTrue(h.System.TryUnlock(TestCatalogs.KeyForBit(0)));
                Assert.IsTrue(h.System.HasUnlocked(TestCatalogs.KeyForBit(0)));
            }
        }

        [Test]
        public void Background_writes_are_coalesced_and_flushed()
        {
            var storage = new MemoryStorage();
            var catalog = TestCatalogs.Create(64);
            using (var system = new AchievementSystem(new AchievementSystemOptions { Catalog = catalog, Storage = storage, BackgroundWrites = true }))
            {
                for (int bit = 0; bit < 64; bit++) system.TryUnlock(TestCatalogs.KeyForBit(bit));
                Assert.IsTrue(system.Store.Flush(5000));
                Assert.LessOrEqual(storage.SaveCount, 64);
                Assert.AreEqual(64, storage.Read(AchievementStore.ActiveSlot).CountUnlocked());
            }
        }

        [Test]
        public void Static_facade_is_safe_before_initialization()
        {
            AchievementManager.Shutdown();
            Assert.IsFalse(AchievementManager.TryUnlock("x"));
            Assert.IsFalse(AchievementManager.HasUnlocked("x"));
            Assert.IsNull(AchievementManager.GetDefinition("x"));
            Assert.AreEqual(AchievementSyncStatus.Disabled, AchievementManager.SyncAsync().Result.Status);
        }

        [Test]
        public void Static_facade_forwards_to_the_initialized_system()
        {
            var catalog = TestCatalogs.Create(3);
            int events = 0;
            Action<AchievementUnlockedEvent> handler = _ => events++;
            AchievementManager.AchievementUnlocked += handler;
            try
            {
                AchievementManager.Initialize(new AchievementSystemOptions { Catalog = catalog, Storage = new MemoryStorage(), BackgroundWrites = false });
                Assert.IsTrue(AchievementManager.TryUnlock(TestCatalogs.KeyForBit(1)));
                Assert.IsFalse(AchievementManager.TryUnlock(TestCatalogs.KeyForBit(1)));
                Assert.IsTrue(AchievementManager.HasUnlocked(TestCatalogs.KeyForBit(1)));
                Assert.AreEqual("Title 1", AchievementManager.GetDefinition(TestCatalogs.KeyForBit(1)).Title);
                Assert.AreEqual(1, events);
            }
            finally
            {
                AchievementManager.AchievementUnlocked -= handler;
                AchievementManager.Shutdown();
            }
        }
    }
}

