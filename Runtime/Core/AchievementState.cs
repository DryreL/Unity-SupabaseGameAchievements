using System;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Compact local achievement state: two bitsets (unlocked, pending server sync) plus the identity
    /// the state belongs to. Not thread-safe on its own; <see cref="AchievementStore"/> owns locking.
    /// </summary>
    /// <remarks>
    /// Binary format, version 1 (little endian):
    /// <code>
    /// offset size field
    ///   0     4  magic "ACHS"
    ///   4     2  format version (1)
    ///   6     2  reserved (0)
    ///   8     8  game id
    ///  16     4  catalog version last used with this state
    ///  20    16  owner user id (Guid bytes; all zero = not yet claimed by an account)
    ///  36     2  bitset length N in bytes (0..512)
    ///  38     N  unlocked bitset
    ///  38+N   N  pending-sync bitset
    ///  38+2N  4  CRC-32 (IEEE) of every preceding byte
    /// </code>
    /// 200 achievements therefore cost 38 + 25 + 25 + 4 = 92 bytes on disk.
    /// </remarks>
    public sealed class AchievementState
    {
        public const int CurrentFormatVersion = 1;
        public const int MaxByteLength = (AchievementCatalog.MaxBitIndex + 1) / 8;

        private const int HeaderLength = 38;
        private const int CrcLength = 4;
        private static readonly byte[] Magic = { (byte)'A', (byte)'C', (byte)'H', (byte)'S' };

        private byte[] _unlocked;
        private byte[] _pending;

        public AchievementState(long gameId, int catalogVersion, Guid ownerId, int byteLength)
        {
            if (byteLength < 0 || byteLength > MaxByteLength) throw new ArgumentOutOfRangeException(nameof(byteLength));
            GameId = gameId;
            CatalogVersion = catalogVersion;
            OwnerId = ownerId;
            _unlocked = new byte[byteLength];
            _pending = new byte[byteLength];
        }

        public long GameId { get; }

        public int CatalogVersion { get; set; }

        /// <summary>Account the state belongs to. <see cref="Guid.Empty"/> until first authenticated sync.</summary>
        public Guid OwnerId { get; set; }

        public int ByteLength => _unlocked.Length;

        public void EnsureByteLength(int byteLength)
        {
            if (byteLength > MaxByteLength) throw new ArgumentOutOfRangeException(nameof(byteLength));
            if (byteLength <= _unlocked.Length) return; // never shrink: bits beyond an older catalog are preserved
            Array.Resize(ref _unlocked, byteLength);
            Array.Resize(ref _pending, byteLength);
        }

        public bool IsUnlocked(int bit) => Get(_unlocked, bit);

        public bool IsPending(int bit) => Get(_pending, bit);

        /// <summary>Sets the unlocked bit. Returns true only on a false → true transition.</summary>
        public bool SetUnlocked(int bit) => Set(_unlocked, bit);

        public bool SetPending(int bit) => Set(_pending, bit);

        public bool ClearPending(int bit)
        {
            if (!Get(_pending, bit)) return false;
            _pending[bit >> 3] &= (byte)~(1 << (bit & 7));
            return true;
        }

        public int CountUnlocked() => PopCount(_unlocked);

        public int CountPending() => PopCount(_pending);

        /// <summary>Highest bit position the state can hold (exclusive).</summary>
        public int BitCapacity => _unlocked.Length * 8;

        public AchievementState Clone()
        {
            var copy = new AchievementState(GameId, CatalogVersion, OwnerId, 0);
            copy._unlocked = (byte[])_unlocked.Clone();
            copy._pending = (byte[])_pending.Clone();
            return copy;
        }

        // ------------------------------------------------------------------
        // Serialization
        // ------------------------------------------------------------------

        public byte[] ToBytes()
        {
            int n = _unlocked.Length;
            var data = new byte[HeaderLength + n + n + CrcLength];
            Buffer.BlockCopy(Magic, 0, data, 0, 4);
            WriteUInt16(data, 4, CurrentFormatVersion);
            WriteUInt16(data, 6, 0);
            WriteInt64(data, 8, GameId);
            WriteInt32(data, 16, CatalogVersion);
            Buffer.BlockCopy(OwnerId.ToByteArray(), 0, data, 20, 16);
            WriteUInt16(data, 36, n);
            Buffer.BlockCopy(_unlocked, 0, data, HeaderLength, n);
            Buffer.BlockCopy(_pending, 0, data, HeaderLength + n, n);
            WriteUInt32(data, data.Length - CrcLength, Crc32.Compute(data, 0, data.Length - CrcLength));
            return data;
        }

        /// <summary>
        /// Parses bytes written by <see cref="ToBytes"/> (or an older format, via migration).
        /// Never throws for bad input; the status explains why parsing failed.
        /// </summary>
        public static AchievementStateReadStatus TryParse(byte[] data, out AchievementState state, out int formatVersion)
        {
            state = null;
            formatVersion = 0;
            if (data == null || data.Length < 6) return AchievementStateReadStatus.Corrupt;
            for (int i = 0; i < 4; i++)
                if (data[i] != Magic[i]) return AchievementStateReadStatus.Corrupt;

            formatVersion = ReadUInt16(data, 4);
            switch (formatVersion)
            {
                case 1:
                    return TryParseV1(data, out state);
                default:
                    // A newer build wrote this file. Callers must not overwrite it.
                    return formatVersion > CurrentFormatVersion
                        ? AchievementStateReadStatus.UnsupportedVersion
                        : AchievementStateReadStatus.Corrupt;
                // Future formats: add "case 2: return TryParseV2(...)" and keep TryParseV1 as a
                // migration path that produces the in-memory model; the next save writes the new format.
            }
        }

        private static AchievementStateReadStatus TryParseV1(byte[] data, out AchievementState state)
        {
            state = null;
            if (data.Length < HeaderLength + CrcLength) return AchievementStateReadStatus.Corrupt;
            int n = ReadUInt16(data, 36);
            if (n > MaxByteLength || data.Length != HeaderLength + n + n + CrcLength) return AchievementStateReadStatus.Corrupt;
            if (ReadUInt32(data, data.Length - CrcLength) != Crc32.Compute(data, 0, data.Length - CrcLength))
                return AchievementStateReadStatus.Corrupt;

            var guidBytes = new byte[16];
            Buffer.BlockCopy(data, 20, guidBytes, 0, 16);
            state = new AchievementState(ReadInt64(data, 8), ReadInt32(data, 16), new Guid(guidBytes), n);
            Buffer.BlockCopy(data, HeaderLength, state._unlocked, 0, n);
            Buffer.BlockCopy(data, HeaderLength + n, state._pending, 0, n);

            // Invariant repair: a pending bit without its unlocked bit can only come from tampering.
            for (int i = 0; i < n; i++) state._pending[i] &= state._unlocked[i];
            return AchievementStateReadStatus.Ok;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool Get(byte[] bits, int bit)
        {
            int index = bit >> 3;
            return bit >= 0 && index < bits.Length && (bits[index] & (1 << (bit & 7))) != 0;
        }

        private static bool Set(byte[] bits, int bit)
        {
            if (bit < 0 || (bit >> 3) >= bits.Length) throw new ArgumentOutOfRangeException(nameof(bit));
            int index = bit >> 3;
            byte mask = (byte)(1 << (bit & 7));
            if ((bits[index] & mask) != 0) return false;
            bits[index] |= mask;
            return true;
        }

        private static int PopCount(byte[] bits)
        {
            int count = 0;
            for (int i = 0; i < bits.Length; i++)
            {
                int b = bits[i];
                while (b != 0)
                {
                    b &= b - 1;
                    count++;
                }
            }
            return count;
        }

        private static void WriteUInt16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }

        private static void WriteInt32(byte[] d, int o, int v) => WriteUInt32(d, o, unchecked((uint)v));

        private static void WriteUInt32(byte[] d, int o, uint v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
        }

        private static void WriteInt64(byte[] d, int o, long v)
        {
            WriteUInt32(d, o, unchecked((uint)v));
            WriteUInt32(d, o + 4, unchecked((uint)(v >> 32)));
        }

        private static int ReadUInt16(byte[] d, int o) => d[o] | (d[o + 1] << 8);

        private static uint ReadUInt32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

        private static int ReadInt32(byte[] d, int o) => unchecked((int)ReadUInt32(d, o));

        private static long ReadInt64(byte[] d, int o) => ReadUInt32(d, o) | ((long)ReadUInt32(d, o + 4) << 32);
    }

    public enum AchievementStateReadStatus
    {
        Ok,
        Corrupt,
        UnsupportedVersion,
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}

