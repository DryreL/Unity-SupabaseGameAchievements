using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>Which screen corner (or edge centre) the toast sits in.</summary>
    public enum AchievementToastCorner
    {
        BottomRight = 0,
        BottomLeft = 1,
        TopRight = 2,
        TopLeft = 3,
        BottomCenter = 4,
        TopCenter = 5,
    }

    /// <summary>How the toast appears and disappears.</summary>
    public enum AchievementToastAnimation
    {
        /// <summary>Slides in from the nearest screen edge while fading.</summary>
        Slide = 0,

        /// <summary>Fades in place.</summary>
        Fade = 1,

        /// <summary>Fades in while growing from a slightly smaller size.</summary>
        Pop = 2,
    }

    /// <summary>
    /// Everything about how the achievement toast looks and behaves, as plain data. The Achievement Dashboard's
    /// Overlay tab edits it and writes <c>Resources/Achievements/overlay.json</c>; <see cref="UnityAchievementManager"/>
    /// loads that file when it starts and hands it to <see cref="UnityAchievementOverlay.ApplySettings"/>.
    /// Assets (sound, font, custom prefab) are stored as <c>Resources</c> paths, so the file works in builds.
    /// </summary>
    [Serializable]
    public sealed class AchievementOverlaySettings
    {
        public const string DefaultResource = "Achievements/overlay";

        // ---- text -------------------------------------------------------------------------------------------
        public string headerText = "";
        public string headerLocalizationTable = "ST_Achievements";
        public string headerLocalizationKey = "achievement_unlocked";

        // ---- colors (#RRGGBB or #RRGGBBAA) ------------------------------------------------------------------
        public string backgroundColor = "#14121D";
        public string backgroundColor2 = "#0A090F";
        public string headerColor = "#FFFFFF";
        public string titleColor = "#FFFFFF";
        public string descriptionColor = "#D1D1E0";
        public string shadowColor = "#000000B8";
        public bool shadowEnabled = true;
        public float shadowDistance = 8f;
        public int cornerRadius = 14;

        // ---- fonts ------------------------------------------------------------------------------------------
        public string fontResource = "";
        public int headerFontSize = 16;
        public int titleFontSize = 24;
        public int descriptionFontSize = 18;

        // ---- placement --------------------------------------------------------------------------------------
        [JsonConverter(typeof(StringEnumConverter))]
        public AchievementToastCorner corner = AchievementToastCorner.BottomRight;
        public float marginX;
        public float marginY;
        public float scale = 1f;
        public int sortingOrder = 32000;

        // ---- motion and timing (seconds) --------------------------------------------------------------------
        [JsonConverter(typeof(StringEnumConverter))]
        public AchievementToastAnimation animation = AchievementToastAnimation.Slide;
        public float enterDuration = 0.25f;
        public float holdDuration = 4.5f;
        public float exitDuration = 0.5f;
        public float gapDuration;
        public float localizationGrace = 0.2f;

        // ---- sound ------------------------------------------------------------------------------------------
        public string soundResource = "";
        public float volume = 0.8f;

        // ---- your own toast ---------------------------------------------------------------------------------
        public string toastPrefabResource = "";

        private static readonly JsonSerializerSettings JsonOptions = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Formatting = Formatting.Indented,
        };

        public static AchievementOverlaySettings CreateDefault() => new AchievementOverlaySettings();

        [JsonIgnore]
        public bool UsesCustomPrefab => !string.IsNullOrWhiteSpace(toastPrefabResource);

        /// <summary>Clamps every value into a range the overlay can render, and repairs blank colors.</summary>
        public AchievementOverlaySettings Sanitize()
        {
            var fallback = CreateDefault();
            headerText = headerText ?? "";
            fontResource = (fontResource ?? "").Trim();
            soundResource = (soundResource ?? "").Trim();
            toastPrefabResource = (toastPrefabResource ?? "").Trim();

            backgroundColor = ColorOr(backgroundColor, fallback.backgroundColor);
            backgroundColor2 = ColorOr(backgroundColor2, fallback.backgroundColor2);
            headerColor = ColorOr(headerColor, fallback.headerColor);
            titleColor = ColorOr(titleColor, fallback.titleColor);
            descriptionColor = ColorOr(descriptionColor, fallback.descriptionColor);
            shadowColor = ColorOr(shadowColor, fallback.shadowColor);

            cornerRadius = Mathf.Clamp(cornerRadius, 0, 30);
            shadowDistance = Mathf.Clamp(Finite(shadowDistance, 8f), 0f, 40f);
            headerFontSize = Mathf.Clamp(headerFontSize, 1, 200);
            titleFontSize = Mathf.Clamp(titleFontSize, 1, 200);
            descriptionFontSize = Mathf.Clamp(descriptionFontSize, 1, 200);
            marginX = Mathf.Clamp(Finite(marginX, 0f), -2000f, 2000f);
            marginY = Mathf.Clamp(Finite(marginY, 0f), -2000f, 2000f);
            scale = Mathf.Clamp(Finite(scale, 1f), 0.25f, 4f);
            sortingOrder = Mathf.Clamp(sortingOrder, short.MinValue, short.MaxValue);
            enterDuration = Mathf.Clamp(Finite(enterDuration, 0.25f), 0f, 10f);
            holdDuration = Mathf.Clamp(Finite(holdDuration, 4.5f), 0f, 60f);
            exitDuration = Mathf.Clamp(Finite(exitDuration, 0.5f), 0f, 10f);
            gapDuration = Mathf.Clamp(Finite(gapDuration, 0f), 0f, 30f);
            localizationGrace = Mathf.Clamp(Finite(localizationGrace, 0.2f), 0f, 5f);
            volume = Mathf.Clamp01(Finite(volume, 0.8f));

            if (!Enum.IsDefined(typeof(AchievementToastCorner), corner)) corner = AchievementToastCorner.BottomRight;
            if (!Enum.IsDefined(typeof(AchievementToastAnimation), animation)) animation = AchievementToastAnimation.Slide;
            return this;
        }

        /// <summary>Parses a settings file. Null, blank or unreadable text gives the defaults, never an exception.</summary>
        public static AchievementOverlaySettings FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return CreateDefault();
            try
            {
                var parsed = JsonConvert.DeserializeObject<AchievementOverlaySettings>(json, JsonOptions);
                return (parsed ?? CreateDefault()).Sanitize();
            }
            catch (Exception)
            {
                return CreateDefault();
            }
        }

        /// <summary>True when <paramref name="json"/> can be read at all (used to tell the user their file is broken).</summary>
        public static bool TryParse(string json, out AchievementOverlaySettings settings)
        {
            settings = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                settings = JsonConvert.DeserializeObject<AchievementOverlaySettings>(json, JsonOptions)?.Sanitize();
                return settings != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The file text: pretty printed, LF line endings, ending in a newline.</summary>
        public string ToJson() => JsonConvert.SerializeObject(Clone().Sanitize(), JsonOptions).Replace("\r\n", "\n") + "\n";

        public AchievementOverlaySettings Clone() => (AchievementOverlaySettings)MemberwiseClone();

        // ---- colors -----------------------------------------------------------------------------------------

        /// <summary>Reads <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the # is optional).</summary>
        public static bool TryParseColor(string hex, out Color32 color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim().TrimStart('#');

            if (s.Length == 3 || s.Length == 4)
            {
                var expanded = new char[s.Length * 2];
                for (int i = 0; i < s.Length; i++) expanded[i * 2] = expanded[i * 2 + 1] = s[i];
                s = new string(expanded);
            }
            if (s.Length != 6 && s.Length != 8) return false;

            for (int i = 0; i < s.Length; i++)
                if (!Uri.IsHexDigit(s[i])) return false;

            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            byte a = s.Length == 8 ? Convert.ToByte(s.Substring(6, 2), 16) : (byte)255;
            color = new Color32(r, g, b, a);
            return true;
        }

        /// <summary>The color for a hex string, or <paramref name="fallback"/> when it cannot be read.</summary>
        public static Color32 ColorOrDefault(string hex, Color32 fallback) => TryParseColor(hex, out var c) ? c : fallback;

        /// <summary><c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when the color is not fully opaque.</summary>
        public static string ToHex(Color32 color) =>
            color.a == 255
                ? "#" + color.r.ToString("X2") + color.g.ToString("X2") + color.b.ToString("X2")
                : "#" + color.r.ToString("X2") + color.g.ToString("X2") + color.b.ToString("X2") + color.a.ToString("X2");

        [JsonIgnore] public Color32 BackgroundColor => ColorOrDefault(backgroundColor, new Color32(20, 18, 29, 255));
        [JsonIgnore] public Color32 BackgroundColor2 => ColorOrDefault(backgroundColor2, new Color32(10, 9, 15, 255));
        [JsonIgnore] public Color32 HeaderColor => ColorOrDefault(headerColor, new Color32(255, 255, 255, 255));
        [JsonIgnore] public Color32 TitleColor => ColorOrDefault(titleColor, new Color32(255, 255, 255, 255));
        [JsonIgnore] public Color32 DescriptionColor => ColorOrDefault(descriptionColor, new Color32(209, 209, 224, 255));
        [JsonIgnore] public Color32 ShadowColor => ColorOrDefault(shadowColor, new Color32(0, 0, 0, 184));

        private static string ColorOr(string value, string fallback) => TryParseColor(value, out var c) ? ToHex(c) : fallback;

        private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }
}
