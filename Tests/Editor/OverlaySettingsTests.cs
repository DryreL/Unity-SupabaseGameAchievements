using System;
using System.IO;
using DryreLHub.SupabaseGameAchievements.Editor;
using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class OverlaySettingsTests
    {
        [Test]
        public void The_defaults_reproduce_the_toast_the_game_always_had()
        {
            var s = AchievementOverlaySettings.CreateDefault();

            Assert.AreEqual(AchievementToastCorner.BottomRight, s.corner);
            Assert.AreEqual(AchievementToastAnimation.Slide, s.animation);
            Assert.AreEqual(0.25f, s.enterDuration);
            Assert.AreEqual(4.5f, s.holdDuration);
            Assert.AreEqual(0.5f, s.exitDuration);
            Assert.AreEqual(0.2f, s.localizationGrace);
            Assert.AreEqual(0.8f, s.volume);
            Assert.AreEqual(32000, s.sortingOrder);
            Assert.AreEqual(14, s.cornerRadius);
            Assert.AreEqual(new Color32(20, 18, 29, 255), s.BackgroundColor);
            Assert.AreEqual(new Color32(10, 9, 15, 255), s.BackgroundColor2);
            Assert.AreEqual(new Color32(255, 255, 255, 255), s.TitleColor);
            Assert.AreEqual(new Color32(0, 0, 0, 184), s.ShadowColor);
            Assert.IsFalse(s.UsesCustomPrefab);
            Assert.AreEqual("", s.headerText, "empty keeps the per-language default header");
        }

        [Test]
        public void Settings_survive_a_round_trip_through_the_file()
        {
            var s = AchievementOverlaySettings.CreateDefault();
            s.headerText = "TROPHY EARNED";
            s.titleColor = "#FF8800";
            s.corner = AchievementToastCorner.TopLeft;
            s.animation = AchievementToastAnimation.Pop;
            s.holdDuration = 7.5f;
            s.gapDuration = 1.25f;
            s.soundResource = "Achievements/sounds/ding";
            s.toastPrefabResource = "Achievements/AchievementToast";

            var back = AchievementOverlaySettings.FromJson(s.ToJson());

            Assert.AreEqual("TROPHY EARNED", back.headerText);
            Assert.AreEqual("#FF8800", back.titleColor);
            Assert.AreEqual(AchievementToastCorner.TopLeft, back.corner);
            Assert.AreEqual(AchievementToastAnimation.Pop, back.animation);
            Assert.AreEqual(7.5f, back.holdDuration);
            Assert.AreEqual(1.25f, back.gapDuration);
            Assert.AreEqual("Achievements/sounds/ding", back.soundResource);
            Assert.IsTrue(back.UsesCustomPrefab);
        }

        [Test]
        public void The_file_is_readable_text_with_named_enums_and_lf_line_endings()
        {
            var s = AchievementOverlaySettings.CreateDefault();
            s.corner = AchievementToastCorner.TopCenter;
            string json = s.ToJson();

            StringAssert.Contains("\"corner\": \"TopCenter\"", json);
            StringAssert.Contains("\"animation\": \"Slide\"", json);
            Assert.IsFalse(json.Contains("\r"), "Newtonsoft indents with CRLF on Windows; the file must not");
            Assert.IsTrue(json.EndsWith("}\n"));
            StringAssert.DoesNotContain("UsesCustomPrefab", json, "computed helpers are not settings");
            StringAssert.DoesNotContain("TitleColor", json, "only the hex strings are stored");
        }

        [Test]
        public void A_file_with_only_some_settings_keeps_the_defaults_for_the_rest()
        {
            var s = AchievementOverlaySettings.FromJson("{ \"holdDuration\": 9, \"somethingNew\": 1 }");

            Assert.AreEqual(9f, s.holdDuration);
            Assert.AreEqual(0.25f, s.enterDuration, "unmentioned settings stay at their defaults");
            Assert.AreEqual(AchievementToastCorner.BottomRight, s.corner);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not json at all")]
        [TestCase("{ \"corner\": \"Middle\" }")]
        [TestCase("[1,2,3]")]
        public void Unreadable_text_gives_the_defaults_and_never_throws(string json)
        {
            var s = AchievementOverlaySettings.FromJson(json);

            Assert.AreEqual(4.5f, s.holdDuration);
            Assert.IsFalse(AchievementOverlaySettings.TryParse(json, out _), "TryParse tells a broken file from a good one");
        }

        [Test]
        public void Every_value_is_clamped_into_a_range_the_toast_can_render()
        {
            var s = new AchievementOverlaySettings
            {
                cornerRadius = 500, headerFontSize = 0, titleFontSize = -4, descriptionFontSize = 9000,
                scale = 0f, enterDuration = -1f, holdDuration = 1e9f, exitDuration = float.NaN, gapDuration = float.PositiveInfinity,
                volume = 7f, shadowDistance = -3f, marginX = 1e9f, sortingOrder = int.MaxValue,
                corner = (AchievementToastCorner)99, animation = (AchievementToastAnimation)(-1),
                titleColor = "bogus", backgroundColor = null,
            }.Sanitize();

            Assert.AreEqual(30, s.cornerRadius);
            Assert.AreEqual(1, s.headerFontSize);
            Assert.AreEqual(1, s.titleFontSize);
            Assert.AreEqual(200, s.descriptionFontSize);
            Assert.AreEqual(0.25f, s.scale);
            Assert.AreEqual(0f, s.enterDuration);
            Assert.AreEqual(60f, s.holdDuration);
            Assert.AreEqual(0.5f, s.exitDuration, "not a number: the default");
            Assert.AreEqual(0f, s.gapDuration, "infinity: the default");
            Assert.AreEqual(1f, s.volume);
            Assert.AreEqual(0f, s.shadowDistance);
            Assert.AreEqual(2000f, s.marginX);
            Assert.AreEqual(short.MaxValue, s.sortingOrder);
            Assert.AreEqual(AchievementToastCorner.BottomRight, s.corner);
            Assert.AreEqual(AchievementToastAnimation.Slide, s.animation);
            Assert.AreEqual("#FFFFFF", s.titleColor, "an unreadable color falls back to the default");
            Assert.AreEqual("#14121D", s.backgroundColor);
        }

        [TestCase("#FF8800", 255, 136, 0, 255)]
        [TestCase("ff8800", 255, 136, 0, 255)]
        [TestCase("#FF880080", 255, 136, 0, 128)]
        [TestCase("#f80", 255, 136, 0, 255)]
        [TestCase("#f808", 255, 136, 0, 136)]
        [TestCase("  #0A0B0C  ", 10, 11, 12, 255)]
        public void Colors_are_read_in_every_common_hex_form(string hex, int r, int g, int b, int a)
        {
            Assert.IsTrue(AchievementOverlaySettings.TryParseColor(hex, out var c));
            Assert.AreEqual(new Color32((byte)r, (byte)g, (byte)b, (byte)a), c);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("#")]
        [TestCase("#12")]
        [TestCase("#12345")]
        [TestCase("#GGGGGG")]
        [TestCase("red")]
        public void Anything_else_is_not_a_color(string hex)
        {
            Assert.IsFalse(AchievementOverlaySettings.TryParseColor(hex, out _));
        }

        [Test]
        public void Colors_write_back_as_short_hex_and_keep_their_alpha_only_when_needed()
        {
            Assert.AreEqual("#14121D", AchievementOverlaySettings.ToHex(new Color32(20, 18, 29, 255)));
            Assert.AreEqual("#000000B8", AchievementOverlaySettings.ToHex(new Color32(0, 0, 0, 184)));
        }

        [Test]
        public void The_toast_is_placed_against_the_edge_it_slides_through()
        {
            Assert.AreEqual(new Vector2(1, 0), AchievementToastView.CornerAnchor(AchievementToastCorner.BottomRight));
            Assert.AreEqual(new Vector2(0, 0), AchievementToastView.CornerAnchor(AchievementToastCorner.BottomLeft));
            Assert.AreEqual(new Vector2(1, 1), AchievementToastView.CornerAnchor(AchievementToastCorner.TopRight));
            Assert.AreEqual(new Vector2(0, 1), AchievementToastView.CornerAnchor(AchievementToastCorner.TopLeft));
            Assert.AreEqual(new Vector2(0.5f, 0), AchievementToastView.CornerAnchor(AchievementToastCorner.BottomCenter));
            Assert.AreEqual(new Vector2(0.5f, 1), AchievementToastView.CornerAnchor(AchievementToastCorner.TopCenter));

            Assert.AreEqual(-1f, AchievementToastView.SlideSign(AchievementToastCorner.BottomLeft), "bottom corners leave downwards");
            Assert.AreEqual(1f, AchievementToastView.SlideSign(AchievementToastCorner.TopRight), "top corners leave upwards");
        }
    }

    public class OverlaySupportTests
    {
        [TestCase("Assets/Resources/Achievements/sounds/unlock.wav", "Achievements/sounds/unlock")]
        [TestCase("Assets/Resources/Achievements/AchievementToast.prefab", "Achievements/AchievementToast")]
        [TestCase("Assets\\Resources\\Achievements\\fonts\\Main.ttf", "Achievements/fonts/Main")]
        [TestCase("Assets/Game/Resources/Sfx/a.b.ogg", "Sfx/a.b")]
        [TestCase("Assets/Resources/Deep/Resources/x.png", "x")]
        [TestCase("Resources/x.png", "x")]
        [TestCase("Assets/Resources/noextension", "noextension")]
        public void A_resources_path_is_what_follows_the_resources_folder_without_the_extension(string assetPath, string expected)
        {
            Assert.AreEqual(expected, AchievementResourcePaths.FromAssetPath(assetPath));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/Audio/unlock.wav")]
        [TestCase("Assets/ResourcesBackup/unlock.wav")]
        [TestCase("Assets/Resources/")]
        public void An_asset_outside_a_resources_folder_cannot_be_loaded_by_the_game(string assetPath)
        {
            Assert.IsNull(AchievementResourcePaths.FromAssetPath(assetPath));
        }

        [TestCase("Panel", "Panel")]
        [TestCase("Background", "Panel")]
        [TestCase("Icon", "Icon")]
        [TestCase("AchievementIcon", "Icon")]
        [TestCase("Image", "Icon")]
        [TestCase("Header", "Header")]
        [TestCase("Title", "Title")]
        [TestCase("Title Text (TMP)", "Title")]
        [TestCase("Description", "Description")]
        [TestCase("Desc", "Description")]
        [TestCase("Subtitle", "Description")]
        [TestCase("IconBackground", "None")]
        [TestCase("Icon_Bg", "None")]
        [TestCase("Spacer", "None")]
        [TestCase("", "None")]
        [TestCase(null, "None")]
        public void Child_objects_are_recognised_by_their_names(string name, string expected)
        {
            Assert.AreEqual(expected, AchievementToastRoles.FromName(name).ToString());
        }

        [Test]
        public void The_settings_file_is_only_rewritten_when_its_text_changes()
        {
            string path = Path.Combine(Path.GetTempPath(), "overlay-" + Guid.NewGuid().ToString("N"), "overlay.json");
            try
            {
                Assert.IsTrue(AchievementOverlayFile.Write(path, "{\n}\n"), "a new file, in a folder that did not exist yet");
                Assert.IsFalse(AchievementOverlayFile.Write(path, "{\n}\n"), "identical text");
                Assert.IsFalse(AchievementOverlayFile.Write(path, "{\r\n}\r\n"), "only the line endings differ");
                Assert.IsTrue(AchievementOverlayFile.Write(path, "{\n  \"a\": 1\n}\n"));
                Assert.AreEqual("{\n  \"a\": 1\n}\n", File.ReadAllText(path));
                Assert.IsFalse(File.Exists(path + ".tmp"), "no temp file left behind");
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path), true); } catch (IOException) { }
            }
        }
    }
}
