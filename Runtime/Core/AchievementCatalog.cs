using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>How an achievement's icon is composed in the unlock toast.</summary>
    public enum AchievementIconStyle
    {
        /// <summary>One image per achievement that already contains its own background (the default).</summary>
        Combined = 0,

        /// <summary>One shared background image for every achievement, with the achievement's own icon drawn on top.</summary>
        Layered = 1,
    }

    /// <summary>
    /// Read-only, pre-indexed achievement catalog for one game. Built once at startup; every lookup
    /// afterwards is an allocation-free dictionary or array access.
    /// </summary>
    public sealed class AchievementCatalog
    {
        /// <summary>Highest bit index supported (mirrors the database CHECK constraint).</summary>
        public const int MaxBitIndex = 4095;

        /// <summary>Manifest format versions this build understands.</summary>
        public const int SupportedFormatVersion = 1;

        private readonly Dictionary<string, AchievementDefinition> _byKey;
        private readonly Dictionary<long, AchievementDefinition> _byId;
        private readonly AchievementDefinition[] _byBit;
        private readonly AchievementDefinition[] _ordered;

        /// <summary>Fraction of the background left as a margin on each side of the icon in <see cref="AchievementIconStyle.Layered"/>.</summary>
        public const float DefaultIconInset = 0.18f;

        public AchievementCatalog(long gameId, string gameSlug, int catalogVersion, IEnumerable<AchievementDefinition> achievements,
            AchievementIconStyle iconStyle = AchievementIconStyle.Combined, string iconBackground = null, float iconInset = DefaultIconInset)
        {
            if (gameId <= 0) throw new AchievementCatalogException("Catalog gameId must be positive.");
            if (string.IsNullOrEmpty(gameSlug)) throw new AchievementCatalogException("Catalog game slug is required.");
            if (catalogVersion < 1) throw new AchievementCatalogException("Catalog version must be >= 1.");
            if (achievements == null) throw new ArgumentNullException(nameof(achievements));

            GameId = gameId;
            GameSlug = gameSlug;
            CatalogVersion = catalogVersion;
            IconStyle = iconStyle;
            IconBackground = string.IsNullOrEmpty(iconBackground) ? null : iconBackground;
            IconInset = iconInset < 0f ? 0f : iconInset > 0.45f ? 0.45f : iconInset;

            _byKey = new Dictionary<string, AchievementDefinition>(StringComparer.Ordinal);
            _byId = new Dictionary<long, AchievementDefinition>();
            var list = new List<AchievementDefinition>();
            int maxBit = -1;

            foreach (var definition in achievements)
            {
                if (definition == null) throw new AchievementCatalogException("Catalog contains a null achievement.");
                if (_byKey.ContainsKey(definition.Key))
                    throw new AchievementCatalogException("Duplicate achievement key '" + definition.Key + "'.");
                if (_byId.ContainsKey(definition.Id))
                    throw new AchievementCatalogException("Duplicate achievement id " + definition.Id + ".");

                _byKey.Add(definition.Key, definition);
                _byId.Add(definition.Id, definition);
                list.Add(definition);
                if (definition.BitIndex > maxBit) maxBit = definition.BitIndex;
            }

            _byBit = new AchievementDefinition[maxBit + 1];
            foreach (var definition in list)
            {
                if (_byBit[definition.BitIndex] != null)
                    throw new AchievementCatalogException(
                        "Duplicate bit index " + definition.BitIndex + " ('" + _byBit[definition.BitIndex].Key + "' and '" + definition.Key + "').");
                _byBit[definition.BitIndex] = definition;
            }

            list.Sort((a, b) => a.DisplayOrder != b.DisplayOrder ? a.DisplayOrder.CompareTo(b.DisplayOrder) : a.BitIndex.CompareTo(b.BitIndex));
            _ordered = list.ToArray();
        }

        /// <summary>Server games.id.</summary>
        public long GameId { get; }

        public string GameSlug { get; }

        public int CatalogVersion { get; }

        /// <summary>Game-wide icon composition; <see cref="AchievementIconStyle.Combined"/> unless the manifest says otherwise.</summary>
        public AchievementIconStyle IconStyle { get; }

        /// <summary>Engine-specific path of the shared background image (Layered style), or null.</summary>
        public string IconBackground { get; }

        /// <summary>Margin around the icon inside the background, as a fraction of the background size (0..0.45).</summary>
        public float IconInset { get; }

        public int Count => _ordered.Length;

        /// <summary>Number of bytes a bitset needs to cover every bit index in this catalog.</summary>
        public int BitsetByteLength => (_byBit.Length + 7) / 8;

        /// <summary>All achievements ordered by display order, then bit index.</summary>
        public IReadOnlyList<AchievementDefinition> Achievements => _ordered;

        public bool TryGetByKey(string key, out AchievementDefinition definition)
        {
            if (key == null)
            {
                definition = null;
                return false;
            }
            return _byKey.TryGetValue(key, out definition);
        }

        public bool TryGetById(long id, out AchievementDefinition definition) => _byId.TryGetValue(id, out definition);

        public bool TryGetByBit(int bitIndex, out AchievementDefinition definition)
        {
            if (bitIndex >= 0 && bitIndex < _byBit.Length)
            {
                definition = _byBit[bitIndex];
                return definition != null;
            }
            definition = null;
            return false;
        }

        // ------------------------------------------------------------------
        // Manifest parsing
        // ------------------------------------------------------------------

        /// <summary>
        /// Parses a manifest produced by <c>scripts/export-achievement-catalog.mjs</c>.
        /// Unknown properties are ignored (forward compatible); structural problems throw
        /// <see cref="AchievementCatalogException"/>.
        /// </summary>
        public static AchievementCatalog FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new AchievementCatalogException("Achievement manifest is empty.");

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader);
                }
            }
            catch (JsonException e)
            {
                throw new AchievementCatalogException("Achievement manifest is not valid JSON: " + e.Message, e);
            }

            int formatVersion = ReadInt(root, "formatVersion", 1);
            if (formatVersion > SupportedFormatVersion)
                throw new AchievementCatalogException(
                    "Achievement manifest format " + formatVersion + " is newer than supported format " + SupportedFormatVersion + ".");

            long gameId = ReadLong(root, "gameId", 0);
            string slug = ReadString(root, "game");
            int catalogVersion = ReadInt(root, "catalogVersion", 0);

            if (!(root["achievements"] is JArray items))
                throw new AchievementCatalogException("Achievement manifest has no 'achievements' array.");

            var definitions = new List<AchievementDefinition>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (!(items[i] is JObject item))
                    throw new AchievementCatalogException("achievements[" + i + "] is not an object.");
                try
                {
                    var localization = item["localization"] as JObject;
                    definitions.Add(new AchievementDefinition(
                        id: ReadLong(item, "id", 0),
                        key: ReadString(item, "key"),
                        bitIndex: ReadInt(item, "bitIndex", -1),
                        title: ReadString(item, "title"),
                        description: ReadString(item, "description"),
                        iconPath: ReadString(item, "icon"),
                        iconUrl: ReadString(item, "iconUrl"),
                        hidden: ReadBool(item, "hidden"),
                        displayOrder: ReadInt(item, "displayOrder", 0),
                        retired: ReadBool(item, "retired"),
                        localizationTable: localization != null ? ReadString(localization, "table") : null,
                        titleKey: localization != null ? ReadString(localization, "titleKey") : null,
                        descriptionKey: localization != null ? ReadString(localization, "descriptionKey") : null));
                }
                catch (AchievementCatalogException)
                {
                    throw;
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException)
                {
                    throw new AchievementCatalogException("achievements[" + i + "] is invalid: " + e.Message, e);
                }
            }

            return new AchievementCatalog(gameId, slug, catalogVersion, definitions,
                ReadIconStyle(root), ReadString(root, "iconBackground"), ReadFloat(root, "iconInset", DefaultIconInset));
        }

        // Unknown styles fall back to Combined so a manifest written by a newer tool never breaks an older game.
        private static AchievementIconStyle ReadIconStyle(JObject root)
        {
            string style = ReadString(root, "iconStyle");
            return string.Equals(style, "layered", StringComparison.OrdinalIgnoreCase) ? AchievementIconStyle.Layered : AchievementIconStyle.Combined;
        }

        private static float ReadFloat(JObject obj, string name, float fallback)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new AchievementCatalogException("'" + name + "' must be a number.");
            return Convert.ToSingle(((JValue)token).Value, CultureInfo.InvariantCulture);
        }

        private static string ReadString(JObject obj, string name)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new AchievementCatalogException("'" + name + "' must be a string.");
            return (string)token;
        }

        private static long ReadLong(JObject obj, string name, long fallback)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer) throw new AchievementCatalogException("'" + name + "' must be an integer.");
            return Convert.ToInt64(((JValue)token).Value, CultureInfo.InvariantCulture);
        }

        private static int ReadInt(JObject obj, string name, int fallback)
        {
            long value = ReadLong(obj, name, fallback);
            if (value < int.MinValue || value > int.MaxValue) throw new AchievementCatalogException("'" + name + "' is out of range.");
            return (int)value;
        }

        private static bool ReadBool(JObject obj, string name)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type != JTokenType.Boolean) throw new AchievementCatalogException("'" + name + "' must be a boolean.");
            return (bool)token;
        }
    }

    public sealed class AchievementCatalogException : Exception
    {
        public AchievementCatalogException(string message) : base(message) { }

        public AchievementCatalogException(string message, Exception inner) : base(message, inner) { }
    }
}

