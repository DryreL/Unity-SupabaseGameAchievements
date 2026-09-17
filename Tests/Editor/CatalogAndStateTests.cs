using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class CatalogTests
    {
        private const string ValidManifest = @"{
  ""formatVersion"": 1,
  ""game"": ""test-game"",
  ""gameId"": 7,
  ""catalogVersion"": 4,
  ""futureTopLevelField"": { ""ignored"": true },
  ""achievements"": [
    { ""id"": 102, ""key"": ""lava_walker"", ""bitIndex"": 1, ""title"": ""Lava Walker"", ""description"": ""Cross the lava."", ""displayOrder"": 2 },
    { ""id"": 101, ""key"": ""first_blood"", ""bitIndex"": 0, ""title"": ""First Blood"", ""description"": ""Defeat an enemy."",
      ""icon"": ""achievements/first_blood"", ""hidden"": true, ""displayOrder"": 1, ""someNewField"": 5,
      ""localization"": { ""table"": ""Achievements"", ""titleKey"": ""first_blood_title"", ""descriptionKey"": ""first_blood_description"" } },
    { ""id"": 103, ""key"": ""old_one"", ""bitIndex"": 2, ""title"": ""Old"", ""description"": ""Gone."", ""retired"": true }
  ]
}";

        [Test]
        public void Parses_manifest_and_builds_lookups()
        {
            var catalog = AchievementCatalog.FromJson(ValidManifest);

            Assert.AreEqual(7, catalog.GameId);
            Assert.AreEqual("test-game", catalog.GameSlug);
            Assert.AreEqual(4, catalog.CatalogVersion);
            Assert.AreEqual(3, catalog.Count);
            Assert.AreEqual(1, catalog.BitsetByteLength);

            Assert.IsTrue(catalog.TryGetByKey("first_blood", out var firstBlood));
            Assert.AreEqual(101, firstBlood.Id);
            Assert.IsTrue(firstBlood.Hidden);
            Assert.IsTrue(firstBlood.HasLocalization);
            Assert.AreEqual("first_blood_title", firstBlood.TitleKey);
            Assert.AreEqual("achievements/first_blood", firstBlood.IconPath);

            Assert.IsTrue(catalog.TryGetById(102, out var lava));
            Assert.AreEqual("lava_walker", lava.Key);
            Assert.IsTrue(catalog.TryGetByBit(2, out var old));
            Assert.IsTrue(old.IsRetired);

            CollectionAssert.AreEqual(new[] { "old_one", "first_blood", "lava_walker" }, catalog.Achievements.Select(a => a.Key).ToArray());
        }

        [Test]
        public void Unknown_lookups_fail_without_throwing()
        {
            var catalog = AchievementCatalog.FromJson(ValidManifest);
            Assert.IsFalse(catalog.TryGetByKey("nope", out _));
            Assert.IsFalse(catalog.TryGetByKey(null, out _));
            Assert.IsFalse(catalog.TryGetById(999, out _));
            Assert.IsFalse(catalog.TryGetByBit(-1, out _));
            Assert.IsFalse(catalog.TryGetByBit(4000, out _));
        }

        [Test]
        public void Rejects_malformed_json()
        {
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson("{ \"achievements\": [ "));
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(""));
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson("[]"));
        }

        [Test]
        public void Rejects_structural_errors()
        {
            string Manifest(string items) => "{\"gameId\":7,\"game\":\"g\",\"catalogVersion\":1,\"achievements\":[" + items + "]}";

            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest(
                "{\"id\":1,\"key\":\"a\",\"bitIndex\":0,\"title\":\"A\",\"description\":\"\"},{\"id\":2,\"key\":\"b\",\"bitIndex\":0,\"title\":\"B\",\"description\":\"\"}")),
                "duplicate bit index");
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest(
                "{\"id\":1,\"key\":\"a\",\"bitIndex\":0,\"title\":\"A\",\"description\":\"\"},{\"id\":2,\"key\":\"a\",\"bitIndex\":1,\"title\":\"B\",\"description\":\"\"}")),
                "duplicate key");
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest(
                "{\"id\":1,\"key\":\"a\",\"bitIndex\":0,\"title\":\"A\",\"description\":\"\"},{\"id\":1,\"key\":\"b\",\"bitIndex\":1,\"title\":\"B\",\"description\":\"\"}")),
                "duplicate id");
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest(
                "{\"id\":1,\"key\":\"a\",\"bitIndex\":5000,\"title\":\"A\",\"description\":\"\"}")),
                "bit index out of range");
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest(
                "{\"id\":\"1\",\"key\":\"a\",\"bitIndex\":0,\"title\":\"A\",\"description\":\"\"}")),
                "wrong type");
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(
                "{\"gameId\":0,\"game\":\"g\",\"catalogVersion\":1,\"achievements\":[]}"),
                "missing game id");
        }

        [Test]
        public void Rejects_newer_manifest_format()
        {
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(
                "{\"formatVersion\":2,\"gameId\":7,\"game\":\"g\",\"catalogVersion\":1,\"achievements\":[]}"));
        }
    }

    public class StateSerializationTests
    {
        [Test]
        public void Round_trips_compactly()
        {
            var owner = Guid.NewGuid();
            var state = new AchievementState(7, 3, owner, 25);
            state.SetUnlocked(0);
            state.SetUnlocked(199);
            state.SetPending(199);

            var bytes = state.ToBytes();
            Assert.AreEqual(38 + 25 + 25 + 4, bytes.Length, "200 achievements fit in under 100 bytes");

            Assert.AreEqual(AchievementStateReadStatus.Ok, AchievementState.TryParse(bytes, out var loaded, out int version));
            Assert.AreEqual(1, version);
            Assert.AreEqual(7, loaded.GameId);
            Assert.AreEqual(3, loaded.CatalogVersion);
            Assert.AreEqual(owner, loaded.OwnerId);
            Assert.IsTrue(loaded.IsUnlocked(0));
            Assert.IsTrue(loaded.IsUnlocked(199));
            Assert.IsFalse(loaded.IsPending(0));
            Assert.IsTrue(loaded.IsPending(199));
            Assert.AreEqual(2, loaded.CountUnlocked());
        }

        [Test]
        public void Reads_the_documented_version_1_layout()
        {
            // Golden bytes for format v1: game 7, catalog 2, no owner, 1 byte, unlocked bit 0+1, pending bit 1.
            var state = new AchievementState(7, 2, Guid.Empty, 1);
            state.SetUnlocked(0);
            state.SetUnlocked(1);
            state.SetPending(1);
            var bytes = state.ToBytes();

            CollectionAssert.AreEqual(new byte[] { (byte)'A', (byte)'C', (byte)'H', (byte)'S', 1, 0, 0, 0 }, bytes.Take(8).ToArray());
            Assert.AreEqual(7, BitConverter.ToInt64(bytes, 8));
            Assert.AreEqual(2, BitConverter.ToInt32(bytes, 16));
            Assert.AreEqual(1, BitConverter.ToUInt16(bytes, 36));
            Assert.AreEqual(0b11, bytes[38]);
            Assert.AreEqual(0b10, bytes[39]);
        }

        [Test]
        public void Detects_bit_flips_truncation_and_garbage()
        {
            var state = new AchievementState(7, 1, Guid.Empty, 4);
            state.SetUnlocked(3);
            var bytes = state.ToBytes();

            var flipped = (byte[])bytes.Clone();
            flipped[38] ^= 0x40;
            Assert.AreEqual(AchievementStateReadStatus.Corrupt, AchievementState.TryParse(flipped, out _, out _));
            Assert.AreEqual(AchievementStateReadStatus.Corrupt, AchievementState.TryParse(bytes.Take(bytes.Length - 1).ToArray(), out _, out _));
            Assert.AreEqual(AchievementStateReadStatus.Corrupt, AchievementState.TryParse(new byte[] { 1, 2, 3 }, out _, out _));
            Assert.AreEqual(AchievementStateReadStatus.Corrupt, AchievementState.TryParse(null, out _, out _));
        }

        [Test]
        public void Newer_format_is_reported_not_treated_as_corrupt()
        {
            var bytes = new AchievementState(7, 1, Guid.Empty, 1).ToBytes();
            bytes[4] = 9;
            Assert.AreEqual(AchievementStateReadStatus.UnsupportedVersion, AchievementState.TryParse(bytes, out _, out int version));
            Assert.AreEqual(9, version);
        }

        [Test]
        public void Growing_never_loses_bits()
        {
            var state = new AchievementState(7, 1, Guid.Empty, 1);
            state.SetUnlocked(5);
            state.EnsureByteLength(3);
            state.EnsureByteLength(1); // no shrink
            Assert.AreEqual(3, state.ByteLength);
            Assert.IsTrue(state.IsUnlocked(5));
            Assert.IsTrue(state.SetUnlocked(20));
        }
    }

    public class FileStorageTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "achievement-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        private static AchievementState StateWithBit(int bit)
        {
            var state = new AchievementState(7, 1, Guid.Empty, 2);
            state.SetUnlocked(bit);
            return state;
        }

        [Test]
        public void Missing_slot_is_not_found()
        {
            var storage = new FileAchievementStorage(_directory);
            Assert.AreEqual(AchievementStorageLoadStatus.NotFound, storage.Load("active").Status);
        }

        [Test]
        public void Save_then_load_and_keep_previous_version_as_backup()
        {
            var storage = new FileAchievementStorage(_directory);
            storage.Save("active", StateWithBit(1));
            storage.Save("active", StateWithBit(2));

            var loaded = storage.Load("active");
            Assert.AreEqual(AchievementStorageLoadStatus.Loaded, loaded.Status);
            Assert.IsTrue(loaded.State.IsUnlocked(2));
            Assert.IsTrue(File.Exists(Path.Combine(_directory, "active.bin.bak")));
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "active.bin.tmp")));
        }

        [Test]
        public void Corrupt_primary_recovers_previous_valid_backup_and_is_quarantined()
        {
            var storage = new FileAchievementStorage(_directory);
            storage.Save("active", StateWithBit(1));
            storage.Save("active", StateWithBit(2));
            File.WriteAllBytes(Path.Combine(_directory, "active.bin"), new byte[] { (byte)'A', (byte)'C', (byte)'H', (byte)'S', 1, 0, 9, 9, 9 });

            var loaded = storage.Load("active");
            Assert.AreEqual(AchievementStorageLoadStatus.RecoveredFromBackup, loaded.Status);
            Assert.IsTrue(loaded.State.IsUnlocked(1));
            Assert.IsTrue(Directory.GetFiles(_directory, "active.bin.corrupt-*").Length == 1);
        }

        [Test]
        public void Interrupted_replace_with_missing_primary_uses_backup()
        {
            var storage = new FileAchievementStorage(_directory);
            storage.Save("active", StateWithBit(1));
            storage.Save("active", StateWithBit(2));
            File.Delete(Path.Combine(_directory, "active.bin"));
            File.WriteAllBytes(Path.Combine(_directory, "active.bin.tmp"), new byte[] { 1, 2 }); // torn temp file

            var loaded = storage.Load("active");
            Assert.AreEqual(AchievementStorageLoadStatus.RecoveredFromBackup, loaded.Status);
            Assert.IsTrue(loaded.State.IsUnlocked(1));

            storage.Save("active", StateWithBit(3)); // a later save overwrites the torn temp safely
            Assert.IsTrue(storage.Load("active").State.IsUnlocked(3));
        }

        [Test]
        public void Both_copies_corrupt_reports_corrupt()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(Path.Combine(_directory, "active.bin"), new byte[] { 0, 1, 2, 3, 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(_directory, "active.bin.bak"), new byte[] { 9, 9 });

            var storage = new FileAchievementStorage(_directory);
            Assert.AreEqual(AchievementStorageLoadStatus.Corrupt, storage.Load("active").Status);
            Assert.AreEqual(AchievementStorageLoadStatus.NotFound, storage.Load("active").Status, "corrupt files were moved aside");
        }

        [Test]
        public void Newer_format_is_never_reported_as_loadable()
        {
            var storage = new FileAchievementStorage(_directory);
            storage.Save("active", StateWithBit(1));
            var path = Path.Combine(_directory, "active.bin");
            var bytes = File.ReadAllBytes(path);
            bytes[4] = 2;
            File.WriteAllBytes(path, bytes);

            Assert.AreEqual(AchievementStorageLoadStatus.UnsupportedVersion, storage.Load("active").Status);
            Assert.IsTrue(File.Exists(path), "not quarantined");
        }

        [Test]
        public void Rejects_path_traversal_slots()
        {
            var storage = new FileAchievementStorage(_directory);
            Assert.Throws<ArgumentException>(() => storage.Load("../evil"));
        }
    }
}

