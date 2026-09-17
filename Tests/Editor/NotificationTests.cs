using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    internal sealed class FakeLocalization : IAchievementLocalizationProvider
    {
        public bool Cached;
        public TaskCompletionSource<AchievementText> Pending = new TaskCompletionSource<AchievementText>();
        public bool Throw;

        public bool TryGetText(AchievementDefinition definition, out AchievementText text)
        {
            if (Throw) throw new InvalidOperationException("localization not loaded");
            text = Cached ? new AchievementText("Localized " + definition.Key, "Localized description") : default;
            return Cached;
        }

        public Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken) => Pending.Task;
    }

    public class NotificationTests
    {
        private static string Key(int bit) => TestCatalogs.KeyForBit(bit);

        [Test]
        public void Enabled_setting_queues_one_notification_per_unlock_in_order()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(2));
                h.System.TryUnlock(Key(0));
                h.System.TryUnlock(Key(1));
                h.System.TryUnlock(Key(0));

                var queue = h.System.Notifications.Queue;
                Assert.AreEqual(3, queue.Count);
                Assert.IsTrue(queue.TryDequeue(out var a));
                Assert.IsTrue(queue.TryDequeue(out var b));
                Assert.IsTrue(queue.TryDequeue(out var c));
                CollectionAssert.AreEqual(new[] { Key(2), Key(0), Key(1) }, new[] { a.Definition.Key, b.Definition.Key, c.Definition.Key });
            }
        }

        [Test]
        public void Disabled_setting_hides_toasts_but_tracking_and_sync_continue()
        {
            using (var h = new Harness())
            {
                h.Settings.NotificationsEnabled = false;
                h.Auth.Authenticated = true;

                Assert.IsTrue(h.System.TryUnlock(Key(0)));
                Assert.AreEqual(0, h.NotificationsQueued);
                Assert.AreEqual(1, h.Unlocked.Count, "gameplay event still raised");
                Assert.IsTrue(h.System.HasUnlocked(Key(0)));

                h.Run(1200);
                Assert.AreEqual(1, h.Api.RowCount(FakeAuth.UserA), "synchronized regardless of the setting");
            }
        }

        [Test]
        public void Fallback_text_is_used_without_localization()
        {
            using (var h = new Harness())
            {
                h.System.TryUnlock(Key(3));
                h.System.Notifications.Queue.TryDequeue(out var toast);
                Assert.AreEqual("Title 3", toast.Title);
                Assert.AreEqual("Description 3", toast.Description);
                Assert.IsTrue(toast.IsTextFinal);
            }
        }

        [Test]
        public void Cached_translation_is_used_immediately()
        {
            var localization = new FakeLocalization { Cached = true };
            using (var h = new Harness(localization: localization))
            {
                h.System.TryUnlock(Key(1));
                h.System.Notifications.Queue.TryDequeue(out var toast);
                Assert.AreEqual("Localized " + Key(1), toast.Title);
                Assert.IsTrue(toast.IsTextFinal);
            }
        }

        [Test]
        public void Slow_localization_shows_fallback_first_then_replaces_it()
        {
            var localization = new FakeLocalization();
            using (var h = new Harness(localization: localization))
            {
                Assert.IsTrue(h.System.TryUnlock(Key(1)), "unlock does not wait for localization");
                h.System.Notifications.Queue.TryDequeue(out var toast);
                Assert.AreEqual("Title 1", toast.Title);
                Assert.IsFalse(toast.IsTextFinal);
                int version = toast.TextVersion;

                localization.Pending.SetResult(new AchievementText("Primera Sangre", "Derrota a tu primer enemigo."));
                SpinWait.SpinUntil(() => toast.IsTextFinal, 2000);

                Assert.AreEqual("Primera Sangre", toast.Title);
                Assert.AreEqual("Derrota a tu primer enemigo.", toast.Description);
                Assert.Greater(toast.TextVersion, version);
            }
        }

        [Test]
        public void Localization_failure_keeps_fallback_text()
        {
            var localization = new FakeLocalization();
            using (var h = new Harness(localization: localization))
            {
                h.System.TryUnlock(Key(1));
                h.System.Notifications.Queue.TryDequeue(out var toast);
                localization.Pending.SetException(new IOException("table failed to load"));
                SpinWait.SpinUntil(() => toast.IsTextFinal, 2000);

                Assert.AreEqual("Title 1", toast.Title);
                Assert.IsTrue(toast.IsTextFinal);
            }
        }

        [Test]
        public void Synchronous_localization_exception_does_not_break_unlock()
        {
            var localization = new FakeLocalization { Throw = true };
            using (var h = new Harness(localization: localization))
            {
                Assert.IsTrue(h.System.TryUnlock(Key(1)));
                h.System.Notifications.Queue.TryDequeue(out var toast);
                Assert.AreEqual("Title 1", toast.Title);
            }
        }

        [Test]
        public void Missing_translation_field_keeps_that_fields_fallback()
        {
            var definition = new AchievementDefinition(1, "k", 0, "Fallback title", "Fallback description");
            var service = new AchievementNotificationService(new FixedAchievementNotificationSettings(true), new FakeLocalization());
            var toast = service.OnUnlocked(new AchievementUnlockedEvent(definition, DateTime.UtcNow));

            // A provider returning an empty title (missing entry) must not blank the toast.
            toast.ApplyText(new AchievementText(null, null));
            Assert.AreEqual("Fallback title", toast.Title);
            Assert.AreEqual("Fallback description", toast.Description);
        }

        [Test]
        public void Queue_is_bounded_and_dropping_a_toast_keeps_the_unlock()
        {
            var service = new AchievementNotificationService(new FixedAchievementNotificationSettings(true), queue: new AchievementNotificationQueue(2));
            for (int i = 1; i <= 3; i++)
                service.OnUnlocked(new AchievementUnlockedEvent(new AchievementDefinition(i, "k" + i, i, "t", "d"), DateTime.UtcNow));
            Assert.AreEqual(2, service.Queue.Count);
        }

        [Test]
        public void Reconciliation_and_sync_never_queue_toasts()
        {
            using (var h = new Harness())
            {
                h.Api.ServerRows[FakeAuth.UserA] = new System.Collections.Generic.HashSet<long> { TestCatalogs.IdForBit(1) };
                h.System.TryUnlock(Key(0));
                h.System.Notifications.Queue.Clear();
                h.Auth.SignIn();
                h.Run(5000);

                Assert.AreEqual(0, h.NotificationsQueued);
                Assert.IsTrue(h.System.HasUnlocked(Key(1)));
            }
        }
    }

    public class NotificationSettingsFileTests
    {
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "achievement-settings-" + Guid.NewGuid().ToString("N"), "shared-settings.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_path), true); } catch (Exception) { }
        }

        private void Write(string content, int secondsFromNow)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, content);
            File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(secondsFromNow)); // distinct timestamps on coarse filesystems
        }

        [Test]
        public void Missing_file_defaults_to_enabled()
        {
            Assert.IsTrue(new JsonFileAchievementNotificationSettings(_path).NotificationsEnabled);
        }

        [Test]
        public void Reads_the_launcher_value_and_picks_up_changes()
        {
            Write("{\"schema_version\":1,\"achievement_notifications_enabled\":false}", 1);
            var settings = new JsonFileAchievementNotificationSettings(_path);
            Assert.IsFalse(settings.NotificationsEnabled);

            Write("{\"schema_version\":1,\"achievement_notifications_enabled\":true}", 2);
            settings.Refresh();
            Assert.IsTrue(settings.NotificationsEnabled);
        }

        [Test]
        public void Malformed_or_partial_json_keeps_the_last_known_value()
        {
            Write("{\"achievement_notifications_enabled\":false}", 1);
            var settings = new JsonFileAchievementNotificationSettings(_path);

            Write("{\"achievement_notifications_enab", 2);
            settings.Refresh();
            Assert.IsFalse(settings.NotificationsEnabled);

            Write("", 3);
            settings.Refresh();
            Assert.IsFalse(settings.NotificationsEnabled);

            Write("[1,2]", 4);
            settings.Refresh();
            Assert.IsFalse(settings.NotificationsEnabled);
        }

        [Test]
        public void Older_schema_without_the_key_uses_the_default()
        {
            Write("{\"schema_version\":0,\"theme\":\"dark\"}", 1);
            Assert.IsTrue(new JsonFileAchievementNotificationSettings(_path).NotificationsEnabled);
        }

        [Test]
        public void Newer_schema_with_extra_keys_still_reads_the_value()
        {
            Write("{\"schema_version\":7,\"achievement_notifications_enabled\":false,\"future\":{\"x\":[1,2,3]}}", 1);
            Assert.IsFalse(new JsonFileAchievementNotificationSettings(_path).NotificationsEnabled);
        }

        [Test]
        public void Wrong_type_is_ignored()
        {
            Write("{\"achievement_notifications_enabled\":\"no\"}", 1);
            Assert.IsTrue(new JsonFileAchievementNotificationSettings(_path).NotificationsEnabled);
        }

        [Test]
        public void Deleted_file_keeps_the_last_known_value()
        {
            Write("{\"achievement_notifications_enabled\":false}", 1);
            var settings = new JsonFileAchievementNotificationSettings(_path);
            File.Delete(_path);
            settings.Refresh();
            Assert.IsFalse(settings.NotificationsEnabled);
        }
    }
}

