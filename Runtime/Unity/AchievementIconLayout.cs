using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>Which icon composition the Unity host uses: follow the manifest, or force one.</summary>
    public enum AchievementIconStyleSetting
    {
        /// <summary>Use the manifest's <c>iconStyle</c> (Combined when it has none).</summary>
        Auto = 0,

        /// <summary>One image per achievement that already contains its background.</summary>
        Combined = 1,

        /// <summary>A shared background image with each achievement's own icon drawn on top.</summary>
        Layered = 2,
    }

    /// <summary>
    /// Pure rect math for the Layered icon style: shrinks an icon's rect towards its own centre so it sits
    /// inside the shared background, whatever anchors/pivot a custom toast prefab uses.
    /// </summary>
    internal static class AchievementIconLayout
    {
        internal struct RectSpec
        {
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 SizeDelta;
            public Vector2 AnchoredPosition;
        }

        public static RectSpec Capture(RectTransform rect) => new RectSpec
        {
            AnchorMin = rect.anchorMin,
            AnchorMax = rect.anchorMax,
            Pivot = rect.pivot,
            SizeDelta = rect.sizeDelta,
            AnchoredPosition = rect.anchoredPosition,
        };

        public static void Apply(RectTransform rect, RectSpec spec)
        {
            rect.anchorMin = spec.AnchorMin;
            rect.anchorMax = spec.AnchorMax;
            rect.pivot = spec.Pivot;
            rect.sizeDelta = spec.SizeDelta;
            rect.anchoredPosition = spec.AnchoredPosition;
        }

        /// <summary>
        /// Returns the rect shrunk by <paramref name="inset"/> (a fraction of the size) on every side, keeping
        /// its centre. Fixed-size rects (point anchors) shrink their size; stretched rects move their anchors
        /// inwards by the same fraction of the anchored span.
        /// </summary>
        public static RectSpec Inset(RectSpec spec, float inset)
        {
            inset = Mathf.Clamp(inset, 0f, 0.45f);
            if (inset <= 0f) return spec;

            bool stretched = !Mathf.Approximately(spec.AnchorMin.x, spec.AnchorMax.x) || !Mathf.Approximately(spec.AnchorMin.y, spec.AnchorMax.y);
            var result = spec;

            if (stretched)
            {
                Vector2 span = spec.AnchorMax - spec.AnchorMin;
                result.AnchorMin = spec.AnchorMin + span * inset;
                result.AnchorMax = spec.AnchorMax - span * inset;
                return result;
            }

            Vector2 shrunk = spec.SizeDelta * (1f - 2f * inset);
            // Keep the centre: with pivot p the rect's centre sits at pos + (0.5 - p) * size.
            result.SizeDelta = shrunk;
            result.AnchoredPosition = spec.AnchoredPosition + Vector2.Scale(new Vector2(0.5f, 0.5f) - spec.Pivot, spec.SizeDelta - shrunk);
            return result;
        }
    }
}
