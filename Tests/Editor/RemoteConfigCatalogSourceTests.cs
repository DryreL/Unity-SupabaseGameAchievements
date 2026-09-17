using System;
using System.IO;
using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class RemoteConfigCatalogSourceTests
    {
        private string _cachePath;

        [SetUp]
        public void SetUp()
        {
            _cachePath = Path.Combine(Path.GetTempPath(), "remote-catalog-test-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch (Exception) { }
            try { if (File.Exists(_cachePath + ".tmp")) File.Delete(_cachePath + ".tmp"); } catch (Exception) { }
        }

        private static string Manifest(long gameId, int catalogVersion, string key = "remote_only") =>
            "{\"formatVersion\":1,\"game\":\"g\",\"gameId\":" + gameId + ",\"catalogVersion\":" + catalogVersion +
            ",\"achievements\":[{\"id\":9001,\"key\":\"" + key + "\",\"bitIndex\":0,\"title\":\"T\",\"description\":\"D\"}]}";

        private RemoteConfigAchievementCatalogSource MakeSource(Func<string, string> liveReader = null) =>
            new RemoteConfigAchievementCatalogSource("achievement_catalog_g", _cachePath, null, liveReader ?? (_ => null));

        [Test]
        public void No_remote_value_keeps_the_bundled_catalog()
        {
            var bundled = TestCatalogs.Create(3, 1);
            var source = MakeSource();

            var resolved = source.ResolveBest(bundled);

            Assert.AreSame(bundled, resolved);
            Assert.IsFalse(File.Exists(_cachePath), "nothing to cache when Remote Config has no value");
        }

        [Test]
        public void Higher_live_version_wins_and_is_cached()
        {
            var bundled = TestCatalogs.Create(3, 1);
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId, 5));

            var resolved = source.ResolveBest(bundled);

            Assert.AreEqual(5, resolved.CatalogVersion);
            Assert.IsTrue(File.Exists(_cachePath));
            StringAssert.Contains("\"catalogVersion\":5", File.ReadAllText(_cachePath));
        }

        [Test]
        public void Lower_or_equal_live_version_does_not_override_and_is_not_cached()
        {
            var bundled = TestCatalogs.Create(3, 5);
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId, 5));

            var resolved = source.ResolveBest(bundled);

            Assert.AreSame(bundled, resolved);
            Assert.IsFalse(File.Exists(_cachePath), "an equal (not strictly newer) value never overwrites the bundled one");
        }

        [Test]
        public void Wrong_game_id_from_remote_is_rejected()
        {
            var bundled = TestCatalogs.Create(3, 1);
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId + 1, 99));

            var resolved = source.ResolveBest(bundled);

            Assert.AreSame(bundled, resolved);
            Assert.IsFalse(File.Exists(_cachePath));
        }

        [Test]
        public void Malformed_remote_json_is_ignored_without_throwing()
        {
            var bundled = TestCatalogs.Create(3, 1);
            var source = MakeSource(_ => "{ not json ");

            Assert.DoesNotThrow(() =>
            {
                var resolved = source.ResolveBest(bundled);
                Assert.AreSame(bundled, resolved);
            });
        }

        [Test]
        public void Previously_cached_value_is_used_on_a_cold_start_before_any_live_fetch()
        {
            var bundled = TestCatalogs.Create(3, 1);
            File.WriteAllText(_cachePath, Manifest(TestCatalogs.GameId, 8));
            var source = MakeSource(); // no live value yet, e.g. Remote Config hasn't fetched this session

            var resolved = source.ResolveBest(bundled);

            Assert.AreEqual(8, resolved.CatalogVersion);
        }

        [Test]
        public void Cache_never_regresses_to_an_older_or_wrong_game_cached_value()
        {
            var bundled = TestCatalogs.Create(3, 3);
            File.WriteAllText(_cachePath, Manifest(TestCatalogs.GameId, 1)); // stale cache from an older session
            var source = MakeSource();

            Assert.AreSame(bundled, source.ResolveBest(bundled));

            File.WriteAllText(_cachePath, Manifest(TestCatalogs.GameId + 1, 99)); // corrupted/foreign cache
            Assert.AreSame(bundled, source.ResolveBest(bundled));
        }

        [Test]
        public void Live_value_beats_a_lower_cached_value_and_replaces_the_cache()
        {
            var bundled = TestCatalogs.Create(3, 1);
            File.WriteAllText(_cachePath, Manifest(TestCatalogs.GameId, 4));
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId, 9));

            var resolved = source.ResolveBest(bundled);

            Assert.AreEqual(9, resolved.CatalogVersion);
            StringAssert.Contains("\"catalogVersion\":9", File.ReadAllText(_cachePath));
        }

        [Test]
        public void TryRefresh_reports_no_update_when_nothing_newer_is_available()
        {
            var current = TestCatalogs.Create(3, 5);
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId, 5));

            Assert.IsFalse(source.TryRefresh(current, out var refreshed));
            Assert.IsNull(refreshed);
        }

        [Test]
        public void TryRefresh_returns_the_newer_catalog_and_caches_it()
        {
            var current = TestCatalogs.Create(3, 5);
            var source = MakeSource(_ => Manifest(TestCatalogs.GameId, 6));

            Assert.IsTrue(source.TryRefresh(current, out var refreshed));
            Assert.AreEqual(6, refreshed.CatalogVersion);
            Assert.IsTrue(File.Exists(_cachePath));
        }

        [Test]
        public void Constructor_rejects_a_missing_key()
        {
            Assert.Throws<ArgumentException>(() => new RemoteConfigAchievementCatalogSource("", _cachePath));
        }
    }
}
