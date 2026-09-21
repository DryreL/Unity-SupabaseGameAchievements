using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class IconStyleTests
    {
        private const string Achievements = "\"achievements\": [ { \"id\": 1, \"key\": \"a\", \"bitIndex\": 0, \"title\": \"A\", \"description\": \"d\" } ]";

        private static string Manifest(string extra) =>
            "{ \"formatVersion\": 1, \"game\": \"g\", \"gameId\": 7, \"catalogVersion\": 2, " + extra + Achievements + " }";

        // ---- manifest parsing ------------------------------------------------------------------------------

        [Test]
        public void A_manifest_without_an_icon_style_is_combined()
        {
            var catalog = AchievementCatalog.FromJson(Manifest(""));

            Assert.AreEqual(AchievementIconStyle.Combined, catalog.IconStyle);
            Assert.IsNull(catalog.IconBackground);
            Assert.AreEqual(AchievementCatalog.DefaultIconInset, catalog.IconInset, 0.0001f);
        }

        [Test]
        public void A_layered_manifest_carries_the_background_and_inset()
        {
            var catalog = AchievementCatalog.FromJson(Manifest("\"iconStyle\": \"layered\", \"iconBackground\": \"images/background\", \"iconInset\": 0.25, "));

            Assert.AreEqual(AchievementIconStyle.Layered, catalog.IconStyle);
            Assert.AreEqual("images/background", catalog.IconBackground);
            Assert.AreEqual(0.25f, catalog.IconInset, 0.0001f);
        }

        [TestCase("Layered")]
        [TestCase("LAYERED")]
        public void The_icon_style_name_is_case_insensitive(string style)
        {
            Assert.AreEqual(AchievementIconStyle.Layered, AchievementCatalog.FromJson(Manifest("\"iconStyle\": \"" + style + "\", ")).IconStyle);
        }

        [Test]
        public void An_unknown_icon_style_falls_back_to_combined_instead_of_breaking_the_game()
        {
            Assert.AreEqual(AchievementIconStyle.Combined, AchievementCatalog.FromJson(Manifest("\"iconStyle\": \"hologram\", ")).IconStyle);
        }

        [TestCase("-1", 0f)]
        [TestCase("0.9", 0.45f)]
        public void The_inset_is_clamped(string value, float expected)
        {
            Assert.AreEqual(expected, AchievementCatalog.FromJson(Manifest("\"iconInset\": " + value + ", ")).IconInset, 0.0001f);
        }

        [Test]
        public void A_non_numeric_inset_is_a_manifest_error()
        {
            Assert.Throws<AchievementCatalogException>(() => AchievementCatalog.FromJson(Manifest("\"iconInset\": \"wide\", ")));
        }

        // ---- layered icon rect math ------------------------------------------------------------------------

        private static AchievementIconLayout.RectSpec FixedRect(Vector2 pivot, Vector2 position, float size) => new AchievementIconLayout.RectSpec
        {
            AnchorMin = new Vector2(0, 0.5f),
            AnchorMax = new Vector2(0, 0.5f),
            Pivot = pivot,
            SizeDelta = new Vector2(size, size),
            AnchoredPosition = position,
        };

        private static Vector2 Centre(AchievementIconLayout.RectSpec r) => r.AnchoredPosition + Vector2.Scale(new Vector2(0.5f, 0.5f) - r.Pivot, r.SizeDelta);

        [Test]
        public void Insetting_a_fixed_rect_shrinks_it_around_its_own_centre()
        {
            var original = FixedRect(new Vector2(0, 0.5f), new Vector2(20, 0), 72);

            var inset = AchievementIconLayout.Inset(original, 0.25f);

            Assert.AreEqual(36f, inset.SizeDelta.x, 0.001f);
            Assert.AreEqual(36f, inset.SizeDelta.y, 0.001f);
            Assert.AreEqual(Centre(original).x, Centre(inset).x, 0.001f);
            Assert.AreEqual(Centre(original).y, Centre(inset).y, 0.001f);
        }

        [TestCase(0f, 0f)]
        [TestCase(0.5f, 0.5f)]
        [TestCase(1f, 1f)]
        [TestCase(0.3f, 0.8f)]
        public void The_centre_is_kept_for_any_pivot(float pivotX, float pivotY)
        {
            var original = FixedRect(new Vector2(pivotX, pivotY), new Vector2(11, -7), 100);

            var inset = AchievementIconLayout.Inset(original, 0.18f);

            Assert.AreEqual(Centre(original).x, Centre(inset).x, 0.001f);
            Assert.AreEqual(Centre(original).y, Centre(inset).y, 0.001f);
        }

        [Test]
        public void Zero_inset_changes_nothing()
        {
            var original = FixedRect(new Vector2(0, 0.5f), new Vector2(20, 0), 72);
            var same = AchievementIconLayout.Inset(original, 0f);

            Assert.AreEqual(original.SizeDelta, same.SizeDelta);
            Assert.AreEqual(original.AnchoredPosition, same.AnchoredPosition);
        }

        [Test]
        public void A_stretched_rect_moves_its_anchors_inwards_and_keeps_its_offsets()
        {
            var stretched = new AchievementIconLayout.RectSpec
            {
                AnchorMin = new Vector2(0, 0),
                AnchorMax = new Vector2(1, 1),
                Pivot = new Vector2(0.5f, 0.5f),
                SizeDelta = Vector2.zero,
                AnchoredPosition = Vector2.zero,
            };

            var inset = AchievementIconLayout.Inset(stretched, 0.2f);

            Assert.AreEqual(new Vector2(0.2f, 0.2f).x, inset.AnchorMin.x, 0.0001f);
            Assert.AreEqual(0.8f, inset.AnchorMax.y, 0.0001f);
            Assert.AreEqual(Vector2.zero, inset.SizeDelta);
        }

        [Test]
        public void An_oversized_inset_is_clamped_so_the_icon_never_collapses_or_inverts()
        {
            var inset = AchievementIconLayout.Inset(FixedRect(new Vector2(0.5f, 0.5f), Vector2.zero, 100), 5f);

            Assert.AreEqual(10f, inset.SizeDelta.x, 0.001f, "0.45 per side leaves 10% of the size");
        }
    }
}
