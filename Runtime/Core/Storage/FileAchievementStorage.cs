using System;
using System.IO;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Crash-safe file storage. Each slot is <c>&lt;slot&gt;.bin</c> plus <c>&lt;slot&gt;.bin.bak</c>
    /// (the previous valid version). A save writes and fsyncs <c>&lt;slot&gt;.bin.tmp</c>, then swaps it in
    /// with <see cref="File.Replace(string,string,string)"/>, so an interrupted save leaves either the
    /// old primary or the backup readable.
    /// </summary>
    public sealed class FileAchievementStorage : IAchievementStorage
    {
        private const int MaxFileBytes = 64 * 1024;

        private readonly string _directory;

        public FileAchievementStorage(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Storage directory is required.", nameof(directory));
            _directory = directory;
        }

        public string Directory => _directory;

        public AchievementStorageLoadResult Load(string slot)
        {
            string primaryPath = PathFor(slot);
            string backupPath = primaryPath + ".bak";

            var primary = Read(primaryPath, out var primaryState, out var primaryDetail);
            if (primary == AchievementStorageLoadStatus.Loaded)
                return new AchievementStorageLoadResult(AchievementStorageLoadStatus.Loaded, primaryState);
            if (primary == AchievementStorageLoadStatus.UnsupportedVersion)
                return new AchievementStorageLoadResult(primary, null, primaryDetail);

            var backup = Read(backupPath, out var backupState, out var backupDetail);
            if (backup == AchievementStorageLoadStatus.Loaded)
            {
                if (primary == AchievementStorageLoadStatus.Corrupt) Quarantine(primaryPath);
                return new AchievementStorageLoadResult(
                    AchievementStorageLoadStatus.RecoveredFromBackup, backupState,
                    primary == AchievementStorageLoadStatus.NotFound ? "primary missing" : "primary " + primaryDetail);
            }

            if (primary == AchievementStorageLoadStatus.NotFound && backup == AchievementStorageLoadStatus.NotFound)
                return new AchievementStorageLoadResult(AchievementStorageLoadStatus.NotFound, null);
            if (backup == AchievementStorageLoadStatus.UnsupportedVersion)
                return new AchievementStorageLoadResult(backup, null, backupDetail);
            if (primary == AchievementStorageLoadStatus.IoError || backup == AchievementStorageLoadStatus.IoError)
                return new AchievementStorageLoadResult(AchievementStorageLoadStatus.IoError, null, primaryDetail ?? backupDetail);

            if (primary == AchievementStorageLoadStatus.Corrupt) Quarantine(primaryPath);
            if (backup == AchievementStorageLoadStatus.Corrupt) Quarantine(backupPath);
            return new AchievementStorageLoadResult(AchievementStorageLoadStatus.Corrupt, null, primaryDetail ?? backupDetail);
        }

        public void Save(string slot, AchievementState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            byte[] bytes = state.ToBytes();

            System.IO.Directory.CreateDirectory(_directory);
            string primaryPath = PathFor(slot);
            string tempPath = primaryPath + ".tmp";
            string backupPath = primaryPath + ".bak";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true); // fsync: the rename below must never expose unflushed data
            }

            if (!File.Exists(primaryPath))
            {
                File.Move(tempPath, primaryPath);
                return;
            }

            try
            {
                File.Replace(tempPath, primaryPath, backupPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByCopy(tempPath, primaryPath, backupPath);
            }
        }

        public bool Exists(string slot)
        {
            string path = PathFor(slot);
            return File.Exists(path) || File.Exists(path + ".bak");
        }

        public void Delete(string slot)
        {
            string path = PathFor(slot);
            TryDelete(path);
            TryDelete(path + ".bak");
            TryDelete(path + ".tmp");
        }

        private string PathFor(string slot)
        {
            if (string.IsNullOrEmpty(slot) || slot.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || slot.Contains(".."))
                throw new ArgumentException("Invalid storage slot name.", nameof(slot));
            return Path.Combine(_directory, slot + ".bin");
        }

        private static AchievementStorageLoadStatus Read(string path, out AchievementState state, out string detail)
        {
            state = null;
            detail = null;
            byte[] data;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return AchievementStorageLoadStatus.NotFound;
                if (info.Length > MaxFileBytes)
                {
                    detail = "file too large";
                    return AchievementStorageLoadStatus.Corrupt;
                }
                data = File.ReadAllBytes(path);
            }
            catch (FileNotFoundException)
            {
                return AchievementStorageLoadStatus.NotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return AchievementStorageLoadStatus.NotFound;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                detail = e.GetType().Name + ": " + e.Message;
                return AchievementStorageLoadStatus.IoError;
            }

            switch (AchievementState.TryParse(data, out state, out int version))
            {
                case AchievementStateReadStatus.Ok:
                    return AchievementStorageLoadStatus.Loaded;
                case AchievementStateReadStatus.UnsupportedVersion:
                    detail = "format version " + version;
                    return AchievementStorageLoadStatus.UnsupportedVersion;
                default:
                    detail = "corrupt";
                    return AchievementStorageLoadStatus.Corrupt;
            }
        }

        private static void ReplaceByCopy(string tempPath, string primaryPath, string backupPath)
        {
            // Fallback for platforms without an atomic replace. Order keeps a valid copy on disk at
            // every step: backup first, then swap the primary.
            File.Copy(primaryPath, backupPath, true);
            File.Delete(primaryPath);
            File.Move(tempPath, primaryPath);
        }

        private static void Quarantine(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            }
            catch (Exception)
            {
                // Best effort: a corrupt file that stays in place is still ignored by Load.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }
}

