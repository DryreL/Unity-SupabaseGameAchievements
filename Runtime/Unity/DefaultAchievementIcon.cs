using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// A plain generated placeholder badge, used when an achievement's real icon cannot be shown and no
    /// custom fallback sprite was configured. Generated in code (not a packaged asset) so the package needs
    /// no binary art of its own and every consumer gets a sane default with zero setup; pass a real sprite
    /// to <c>UnityAchievementManager.Config.FallbackIcon</c> (or <see cref="ResourcesAchievementIconProvider"/>'s
    /// constructor) to replace it with real art.
    /// </summary>
    public static class DefaultAchievementIcon
    {
        private const int Size = 64;
        private static Sprite _cached;

        /// <summary>Builds the placeholder sprite once and reuses it for every call afterward.</summary>
        public static Sprite GetOrCreate()
        {
            if (_cached != null) return _cached;

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "DefaultAchievementIcon",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var fill = new Color32(198, 168, 92, 255);   // muted gold
            var ring = new Color32(132, 106, 54, 255);   // darker gold border
            var clear = new Color32(0, 0, 0, 0);
            var pixels = new Color32[Size * Size];

            var center = new Vector2((Size - 1) * 0.5f, (Size - 1) * 0.5f);
            float outerRadius = Size * 0.47f;
            float ringInnerRadius = outerRadius * 0.72f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center);
                    Color32 pixel;
                    if (dist <= ringInnerRadius) pixel = fill;
                    else if (dist <= outerRadius) pixel = ring;
                    else pixel = clear;

                    // 1px soft edge on the outer rim so it doesn't look jagged at small sizes.
                    if (dist > outerRadius - 1f && dist <= outerRadius + 1f)
                        pixel = Color32.Lerp(clear, ring, Mathf.Clamp01(outerRadius + 1f - dist));

                    pixels[y * Size + x] = pixel;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            _cached = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
            _cached.name = "DefaultAchievementIcon";
            return _cached;
        }
    }
}
