using System;
using System.IO;
using System.Text;
using DryreLHub.SupabaseGameAchievements.Editor;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementRuntimeSetupTests
    {
        private static string Jwt(string payloadJson)
        {
            string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Encode("{\"alg\":\"HS256\"}") + "." + Encode(payloadJson) + ".signature";
        }

        [Test]
        public void A_scene_uses_a_script_when_its_yaml_names_the_guid()
        {
            string yaml = "%YAML 1.1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n";

            Assert.IsTrue(AchievementRuntimeSetup.ReferencesScript(yaml, "0123456789abcdef0123456789abcdef"));
            Assert.IsFalse(AchievementRuntimeSetup.ReferencesScript(yaml, "ffffffffffffffffffffffffffffffff"));
            Assert.IsFalse(AchievementRuntimeSetup.ReferencesScript(null, "0123"));
            Assert.IsFalse(AchievementRuntimeSetup.ReferencesScript(yaml, ""));
        }

        [TestCase("UnityAchievementManager.Create(config);", true)]
        [TestCase("var system = AchievementManager.Initialize(options);", true)]
        [TestCase("AchievementManager.TryUnlock(\"a\");", false)]
        [TestCase("// nothing here", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Game_code_that_starts_the_system_is_recognised(string source, bool expected)
        {
            Assert.AreEqual(expected, AchievementRuntimeSetup.CodeStartsSystem(source));
        }

        private static AchievementRuntimeSetup.Report StartedIn(params string[] sceneFiles)
        {
            var report = new AchievementRuntimeSetup.Report();
            foreach (string file in sceneFiles)
            {
                report.StartedBy.Add("AchievementBootstrap in " + file);
                report.BootstrapScenes.Add(file);
            }
            return report;
        }

        [Test]
        public void A_scene_without_a_bootstrap_is_flagged_when_only_other_scenes_have_one()
        {
            var report = StartedIn("MainMenu_v1.3.unity");

            Assert.IsTrue(AchievementRuntimeSetup.SceneLacksStarter(report, "Assets/Scenes/Game/Game_v1.3.unity"), "Play here would leave achievements off");
            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(report, "Assets/Scenes/MainMenu_v1.3.unity"));
            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(report, "Assets/Scenes/mainmenu_v1.3.UNITY"), "file names compare without regard to case");
        }

        [Test]
        public void Nothing_is_flagged_when_the_system_starts_everywhere_or_nowhere()
        {
            var everywhere = StartedIn("MainMenu.unity");
            everywhere.StartedElsewhere = true; // game code or a prefab starts it in every scene

            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(everywhere, "Assets/Game.unity"));
            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(new AchievementRuntimeSetup.Report(), "Assets/Game.unity"), "not started at all is a different warning");
            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(null, "Assets/Game.unity"));
            Assert.IsFalse(AchievementRuntimeSetup.SceneLacksStarter(StartedIn("A.unity"), ""), "an untitled scene has no file to compare");
        }

        [Test]
        public void Only_public_keys_may_be_written_into_a_scene()
        {
            Assert.IsTrue(AchievementRuntimeSetup.IsPublicKey("sb_publishable_abc123"));
            Assert.IsTrue(AchievementRuntimeSetup.IsPublicKey(Jwt("{\"iss\":\"supabase\",\"role\":\"anon\"}")), "a legacy anon key is public");
            Assert.IsTrue(AchievementRuntimeSetup.IsPublicKey(Jwt("{\"role\": \"anon\"}")), "whitespace in the payload does not matter");

            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey("sb_secret_abc123"));
            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey(Jwt("{\"role\":\"service_role\"}")), "a legacy service key is a secret");
            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey("eyJnotajwt"));
            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey("eyJ.%%%.x"));
            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey(""));
            Assert.IsFalse(AchievementRuntimeSetup.IsPublicKey(null));
        }
    }

    public class GeneratedFileLineEndingTests
    {
        [Test]
        public void The_rules_file_is_generated_with_lf_on_every_platform()
        {
            string json = new AchievementRuleSet(new[]
            {
                new AchievementRuleDefinition("r1", "a", AchievementRuleKind.Unlock),
                new AchievementRuleDefinition("r2", "b", AchievementRuleKind.Counter, 3),
            }).ToJson();

            Assert.IsFalse(json.Contains("\r"), "Newtonsoft indents with CRLF on Windows; the file must not");
            Assert.IsTrue(json.EndsWith("}\n"));
        }

        [Test]
        public void A_written_rules_file_reads_back_identical_after_line_ending_normalization()
        {
            var set = new AchievementRuleSet(new[] { new AchievementRuleDefinition("r1", "a", AchievementRuleKind.Unlock) });
            string path = Path.Combine(Path.GetTempPath(), "rules-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, set.ToJson(), new UTF8Encoding(false));

                // What the dashboard compares to decide "up to date": the same on Windows and everywhere else.
                Assert.AreEqual(set.ToJson().Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
                Assert.AreEqual(set.ToJson(), File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void The_manifest_and_the_dashboard_file_are_generated_with_lf_too()
        {
            var data = new DashboardData { GameId = 7, GameSlug = "g", CatalogVersion = 2 };
            data.Achievements.Add(new DashboardAchievement { Id = 1, Key = "a", Title = "A", BitIndex = 0 });
            string path = Path.Combine(Path.GetTempPath(), "dash-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert.IsFalse(DashboardData.SerializeManifest(data.BuildManifest()).Contains("\r"));

                data.Save(path);
                Assert.IsFalse(File.ReadAllText(path).Contains("\r"));
            }
            finally
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
            }
        }
    }
}
