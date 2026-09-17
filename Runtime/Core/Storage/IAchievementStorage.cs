namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Durable storage for <see cref="AchievementState"/> blobs, addressed by slot name
    /// ("active", "account-&lt;user id&gt;"). Implementations must make <see cref="Save"/> atomic:
    /// after a crash or failed write, <see cref="Load"/> returns either the previous or the new
    /// state, never a torn mix.
    /// </summary>
    public interface IAchievementStorage
    {
        AchievementStorageLoadResult Load(string slot);

        /// <summary>Atomically replaces the slot. Throws on failure; the previous value must survive.</summary>
        void Save(string slot, AchievementState state);

        bool Exists(string slot);

        void Delete(string slot);
    }

    public enum AchievementStorageLoadStatus
    {
        /// <summary>Nothing stored yet.</summary>
        NotFound,

        Loaded,

        /// <summary>The primary copy was missing or corrupt; the previous valid copy was used.</summary>
        RecoveredFromBackup,

        /// <summary>No valid copy exists. Corrupt data was quarantined; start from an empty state.</summary>
        Corrupt,

        /// <summary>A newer build wrote this data. It must not be overwritten by this build.</summary>
        UnsupportedVersion,

        /// <summary>The data could not be read right now (locked, permissions). Do not overwrite; retry later.</summary>
        IoError,
    }

    public readonly struct AchievementStorageLoadResult
    {
        public AchievementStorageLoadResult(AchievementStorageLoadStatus status, AchievementState state, string detail = null)
        {
            Status = status;
            State = state;
            Detail = detail;
        }

        public AchievementStorageLoadStatus Status { get; }

        /// <summary>Non-null for <see cref="AchievementStorageLoadStatus.Loaded"/> and <see cref="AchievementStorageLoadStatus.RecoveredFromBackup"/>.</summary>
        public AchievementState State { get; }

        public string Detail { get; }

        public bool HasState => State != null;
    }
}

