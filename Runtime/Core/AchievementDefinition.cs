using System;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Immutable description of one achievement, mirrored from the server catalog into the game's
    /// read-only manifest. Identity fields (<see cref="Id"/>, <see cref="Key"/>, <see cref="BitIndex"/>)
    /// never change once an achievement has shipped.
    /// </summary>
    public sealed class AchievementDefinition
    {
        public AchievementDefinition(
            long id,
            string key,
            int bitIndex,
            string title,
            string description,
            string iconPath = null,
            bool hidden = false,
            int displayOrder = 0,
            bool retired = false,
            string localizationTable = null,
            string titleKey = null,
            string descriptionKey = null)
        {
            if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "Achievement id must be positive.");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Achievement key is required.", nameof(key));
            if (bitIndex < 0 || bitIndex > AchievementCatalog.MaxBitIndex)
                throw new ArgumentOutOfRangeException(nameof(bitIndex), "Bit index must be within 0.." + AchievementCatalog.MaxBitIndex + ".");

            Id = id;
            Key = key;
            BitIndex = bitIndex;
            Title = title ?? key;
            Description = description ?? string.Empty;
            IconPath = string.IsNullOrEmpty(iconPath) ? null : iconPath;
            Hidden = hidden;
            DisplayOrder = displayOrder;
            IsRetired = retired;
            LocalizationTable = string.IsNullOrEmpty(localizationTable) ? null : localizationTable;
            TitleKey = string.IsNullOrEmpty(titleKey) ? null : titleKey;
            DescriptionKey = string.IsNullOrEmpty(descriptionKey) ? null : descriptionKey;
        }

        /// <summary>Server primary key (achievements.id). Used for synchronization.</summary>
        public long Id { get; }

        /// <summary>Stable gameplay identifier, e.g. "first_blood".</summary>
        public string Key { get; }

        /// <summary>Immutable position in the local unlock bitset.</summary>
        public int BitIndex { get; }

        /// <summary>Fallback (untranslated) title.</summary>
        public string Title { get; }

        /// <summary>Fallback (untranslated) description.</summary>
        public string Description { get; }

        /// <summary>Engine-specific path of the icon packaged with the game, or null.</summary>
        public string IconPath { get; }

        public bool Hidden { get; }

        public int DisplayOrder { get; }

        /// <summary>Retired achievements can no longer be unlocked; their bit index stays reserved.</summary>
        public bool IsRetired { get; }

        public string LocalizationTable { get; }

        public string TitleKey { get; }

        public string DescriptionKey { get; }

        public bool HasLocalization => LocalizationTable != null && (TitleKey != null || DescriptionKey != null);

        public override string ToString() => Key + " (#" + Id + ", bit " + BitIndex + ")";
    }
}

