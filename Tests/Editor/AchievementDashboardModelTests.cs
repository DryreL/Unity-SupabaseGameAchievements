using System.IO;
using System.Linq;
using DryreLHub.SupabaseGameAchievements.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class AchievementDashboardModelTests
    {
        private static JObject ServerRow(long id, string key, int bit, string title = "Title", bool retired = false) => new JObject
        {
            ["id"] = id,
            ["achievement_key"] = key,
            ["bit_index"] = bit,
            ["title"] = title,
            ["description"] = "Description",
            ["icon_path"] = null,
            ["icon_url"] = null,
            ["hidden"] = false,
            ["is_retired"] = retired,
            ["display_order"] = 0,
            ["localization_table"] = null,
            ["title_key"] = null,
            ["description_key"] = null,
        };

        private static DashboardData Connected() => new DashboardData { GameId = 7, GameSlug = "test-game", CatalogVersion = 3 };

        // ---- sync state ------------------------------------------------------------------------------------

        [Test]
        public void A_never_pushed_entry_is_new()
        {
            var data = Connected();
            Assert.AreEqual(DashboardSyncState.New, data.AddNew().State);
        }

        [Test]
        public void A_row_from_the_server_is_synced_until_a_field_changes()
        {
            var a = DashboardAchievement.FromRow(ServerRow(10, "first", 0));
            Assert.AreEqual(DashboardSyncState.Synced, a.State);

            a.Title = "Edited";
            Assert.AreEqual(DashboardSyncState.Modified, a.State);

            a.Title = "Title";
            Assert.AreEqual(DashboardSyncState.Synced, a.State, "reverting an edit must not leave the entry looking modified");
        }

        [Test]
        public void Every_editable_field_counts_as_a_modification()
        {
            System.Action<DashboardAchievement>[] edits =
            {
                a => a.Description = "x", a => a.IconPath = "x", a => a.IconUrl = "https://x", a => a.Hidden = true,
                a => a.Retired = true, a => a.DisplayOrder = 5, a => a.LocalizationTable = "x", a => a.TitleKey = "x", a => a.DescriptionKey = "x",
            };
            foreach (var edit in edits)
            {
                var a = DashboardAchievement.FromRow(ServerRow(1, "k", 0));
                edit(a);
                Assert.AreEqual(DashboardSyncState.Modified, a.State);
            }
        }

        // ---- payloads --------------------------------------------------------------------------------------

        [Test]
        public void An_update_never_carries_the_immutable_columns()
        {
            var payload = DashboardAchievement.FromRow(ServerRow(10, "first", 4)).BuildUpdatePayload();

            foreach (var forbidden in new[] { "id", "game_id", "achievement_key", "bit_index" })
                Assert.IsFalse(payload.ContainsKey(forbidden), forbidden + " is immutable and must not be PATCHed");
        }

        [Test]
        public void An_insert_carries_the_identity_columns_and_the_game()
        {
            var a = new DashboardAchievement { Key = "first", BitIndex = 4, Title = "First" };
            var payload = a.BuildInsertPayload(7);

            Assert.AreEqual(7, payload.Value<long>("game_id"));
            Assert.AreEqual("first", payload.Value<string>("achievement_key"));
            Assert.AreEqual(4, payload.Value<int>("bit_index"));
            Assert.IsFalse(payload.ContainsKey("id"), "the id is assigned by the database");
        }

        [Test]
        public void Empty_optional_strings_are_sent_as_null_because_the_database_rejects_empty_ones()
        {
            var payload = new DashboardAchievement { Key = "k", Title = "T", IconPath = "", IconUrl = "", LocalizationTable = "" }.BuildUpdatePayload();

            string json = payload.ToString(Newtonsoft.Json.Formatting.None);
            StringAssert.Contains("\"icon_path\":null", json);
            StringAssert.Contains("\"icon_url\":null", json);
            StringAssert.Contains("\"localization_table\":null", json);
            Assert.AreEqual("", payload.Value<string>("description"), "description is NOT NULL, so empty stays empty");
        }

        [Test]
        public void All_insert_payloads_share_the_same_keys_as_a_bulk_post_requires()
        {
            var plain = new DashboardAchievement { Key = "a", BitIndex = 0, Title = "A" };
            var full = new DashboardAchievement { Key = "b", BitIndex = 1, Title = "B", IconUrl = "https://x", LocalizationTable = "T", TitleKey = "t" };

            CollectionAssert.AreEquivalent(
                plain.BuildInsertPayload(7).Properties().Select(p => p.Name),
                full.BuildInsertPayload(7).Properties().Select(p => p.Name));
        }

        // ---- adding ----------------------------------------------------------------------------------------

        [Test]
        public void New_entries_take_the_next_bit_index_counting_retired_ones()
        {
            var data = Connected();
            data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(1, "a", 0)));
            data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(2, "b", 5, retired: true)));

            Assert.AreEqual(6, data.AddNew().BitIndex);
        }

        [Test]
        public void New_entries_get_unique_keys()
        {
            var data = Connected();
            var first = data.AddNew();
            var second = data.AddNew();
            var third = data.AddNew();

            Assert.AreEqual(3, new[] { first.Key, second.Key, third.Key }.Distinct().Count());
            Assert.IsEmpty(DashboardValidator.Validate(second, data.Achievements));
        }

        [Test]
        public void New_entries_default_their_icon_path_to_the_images_folder()
        {
            var a = Connected().AddNew();
            Assert.AreEqual("images/new_achievement", a.IconPath);
        }

        [Test]
        public void The_icon_folder_can_be_renamed_and_slashes_are_normalized()
        {
            var data = Connected();
            data.IconFolder = "\\icons/badges\\";
            Assert.AreEqual("icons/badges/first", data.IconPathFor("first"));

            data.IconFolder = "";
            Assert.AreEqual("first", data.IconPathFor("first"));
        }

        [Test]
        public void An_auto_generated_icon_path_follows_the_key_but_a_hand_written_one_does_not()
        {
            var data = Connected();
            var auto = data.AddNew();
            data.RenameKey(auto, "first_blood");
            Assert.AreEqual("images/first_blood", auto.IconPath);

            var manual = data.AddNew();
            manual.IconPath = "special/art";
            data.RenameKey(manual, "other");
            Assert.AreEqual("special/art", manual.IconPath);
        }

        [Test]
        public void Changing_the_icon_folder_moves_only_unpushed_auto_paths()
        {
            var data = Connected();
            var draft = data.AddNew();
            var manual = data.AddNew();
            manual.IconPath = "special/art";
            var pushedRow = ServerRow(5, "old", 9);
            pushedRow["icon_path"] = "images/old";
            var pushed = DashboardAchievement.FromRow(pushedRow);
            data.Achievements.Add(pushed);

            data.SetIconFolder("badges");

            Assert.AreEqual("badges/" + draft.Key, draft.IconPath);
            Assert.AreEqual("special/art", manual.IconPath);
            Assert.AreEqual("images/old", pushed.IconPath, "a pushed entry's path is already stored on the server");
            Assert.AreEqual(DashboardSyncState.Synced, pushed.State);
        }

        [Test]
        public void An_old_data_file_without_an_icon_folder_gets_the_default()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ \"FormatVersion\": 1, \"Achievements\": [] }");
                Assert.AreEqual("images", DashboardData.Load(path).IconFolder);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---- localization ----------------------------------------------------------------------------------

        private static DashboardAchievement Named(string key, string title, string description) =>
            new DashboardAchievement { Key = key, Title = title, Description = description, BitIndex = 0 };

        [Test]
        public void The_localization_plan_defaults_to_key_title_and_key_description_in_the_default_table()
        {
            var data = Connected();
            data.Achievements.Add(Named("clicked_on_a_link", "Clicked a link", "You clicked a link."));

            var plan = data.BuildLocalizationPlan();

            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual("ST_Achievements", plan[0].Table);
            Assert.AreEqual("clicked_on_a_link_title", plan[0].Key);
            Assert.AreEqual("Clicked a link", plan[0].Text);
            Assert.AreEqual("clicked_on_a_link_description", plan[1].Key);
            Assert.AreEqual("You clicked a link.", plan[1].Text);
        }

        [Test]
        public void The_localization_plan_uses_overrides_where_present_and_defaults_elsewhere()
        {
            var data = Connected();
            var a = Named("boss", "Boss", "Beat the boss.");
            a.LocalizationTable = "ST_Bosses";
            a.TitleKey = "custom_boss_title";
            data.Achievements.Add(a);

            var plan = data.BuildLocalizationPlan();

            Assert.AreEqual("ST_Bosses", plan[0].Table);
            Assert.AreEqual("custom_boss_title", plan[0].Key);
            Assert.AreEqual("ST_Bosses", plan[1].Table);
            Assert.AreEqual("boss_description", plan[1].Key, "only the overridden key changes");
        }

        [Test]
        public void The_localization_plan_lists_a_table_and_key_once()
        {
            var data = Connected();
            data.Achievements.Add(Named("a", "First", "d"));
            var b = Named("b", "Second", "d");
            b.BitIndex = 1;
            b.TitleKey = "a_title";
            data.Achievements.Add(b);

            var plan = data.BuildLocalizationPlan();

            Assert.AreEqual(1, plan.Count(p => p.Key == "a_title"));
            Assert.AreEqual("First", plan.First(p => p.Key == "a_title").Text, "the first achievement to claim a key wins");
        }

        [Test]
        public void Applying_localization_defaults_fills_only_empty_fields()
        {
            var data = Connected();
            var plain = Named("plain", "P", "d");
            var overridden = Named("over", "O", "d");
            overridden.BitIndex = 1;
            overridden.TitleKey = "my_title_key";
            data.Achievements.AddRange(new[] { plain, overridden });

            int changed = data.ApplyLocalizationDefaults();

            Assert.AreEqual(2, changed);
            Assert.AreEqual("ST_Achievements", plain.LocalizationTable);
            Assert.AreEqual("plain_title", plain.TitleKey);
            Assert.AreEqual("plain_description", plain.DescriptionKey);
            Assert.AreEqual("my_title_key", overridden.TitleKey, "a hand-written key is an override");
            Assert.AreEqual("over_description", overridden.DescriptionKey);
            Assert.AreEqual(0, data.ApplyLocalizationDefaults(), "a second run changes nothing");
        }

        [Test]
        public void Localizing_a_synced_achievement_makes_it_modified_so_it_gets_pushed()
        {
            var data = Connected();
            var a = DashboardAchievement.FromRow(ServerRow(5, "synced", 0));
            data.Achievements.Add(a);

            data.ApplyLocalizationDefaults();

            Assert.AreEqual(DashboardSyncState.Modified, a.State);
            Assert.AreEqual("synced_title", a.BuildUpdatePayload().Value<string>("title_key"));
        }

        [Test]
        public void An_auto_localization_key_follows_a_rename_but_an_override_does_not()
        {
            var data = Connected();
            var auto = Named("old", "T", "d");
            auto.LocalizationTable = "ST_Achievements";
            auto.TitleKey = "old_title";
            auto.DescriptionKey = "old_description";
            var manual = Named("old2", "T", "d");
            manual.BitIndex = 1;
            manual.TitleKey = "hand_written";
            data.Achievements.AddRange(new[] { auto, manual });

            data.RenameKey(auto, "new");
            data.RenameKey(manual, "new2");

            Assert.AreEqual("new_title", auto.TitleKey);
            Assert.AreEqual("new_description", auto.DescriptionKey);
            Assert.AreEqual("hand_written", manual.TitleKey);
        }

        [Test]
        public void Localization_setup_names_are_validated()
        {
            Assert.IsNull(DashboardValidator.ValidateLocalizationSetup("ST_Achievements", "Assets/Localization/Tables"));
            Assert.IsNull(DashboardValidator.ValidateLocalizationSetup("ST_Achievements", "Assets\\Localization\\Tables\\"));
            Assert.IsNotEmpty(DashboardValidator.ValidateLocalizationSetup("", "Assets/x"));
            Assert.IsNotEmpty(DashboardValidator.ValidateLocalizationSetup("bad/name", "Assets/x"));
            Assert.IsNotEmpty(DashboardValidator.ValidateLocalizationSetup("bad[name]", "Assets/x"));
            Assert.IsNotEmpty(DashboardValidator.ValidateLocalizationSetup("ST", "Localization/Tables"), "must be inside Assets");
            Assert.IsNotEmpty(DashboardValidator.ValidateLocalizationSetup("ST", "Assets/../Outside"));
        }

        [Test]
        public void An_old_data_file_without_localization_settings_gets_the_defaults()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ \"FormatVersion\": 1, \"Achievements\": [] }");
                var data = DashboardData.Load(path);
                Assert.AreEqual("ST_Achievements", data.DefaultLocalizationTable);
                Assert.AreEqual("Assets/Localization/Tables", data.LocalizationFolder);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---- persistence -----------------------------------------------------------------------------------

        [Test]
        public void Save_and_load_round_trips_including_sync_state()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var data = Connected();
                data.ManifestPath = "Assets/x/achievements.json";
                data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(10, "synced", 0)));
                var edited = DashboardAchievement.FromRow(ServerRow(11, "edited", 1));
                edited.Title = "Edited";
                data.Achievements.Add(edited);
                data.AddNew();

                data.Save(path);
                var loaded = DashboardData.Load(path);

                Assert.AreEqual(7, loaded.GameId);
                Assert.AreEqual(3, loaded.CatalogVersion);
                Assert.AreEqual("Assets/x/achievements.json", loaded.ManifestPath);
                CollectionAssert.AreEqual(
                    new[] { DashboardSyncState.Synced, DashboardSyncState.Modified, DashboardSyncState.New },
                    loaded.Achievements.Select(a => a.State));
                Assert.AreEqual("Edited", loaded.Achievements[1].Title);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Saving_over_an_existing_file_replaces_it_and_leaves_no_temp_file()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var data = Connected();
                data.Save(path);
                data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(1, "a", 0)));
                data.Save(path);

                Assert.AreEqual(1, DashboardData.Load(path).Achievements.Count);
                Assert.IsFalse(File.Exists(path + ".tmp"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void A_missing_file_loads_as_an_empty_dashboard()
        {
            var data = DashboardData.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + System.Guid.NewGuid().ToString("N") + ".json"));
            Assert.AreEqual(0, data.Achievements.Count);
        }

        [Test]
        public void A_corrupt_file_throws_instead_of_being_treated_as_empty()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ not json");
                Assert.That(() => DashboardData.Load(path), Throws.Exception);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void A_file_from_a_newer_format_is_refused()
        {
            string path = Path.Combine(Path.GetTempPath(), "dashboard-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{ \"FormatVersion\": 99, \"Achievements\": [] }");
                Assert.Throws<InvalidDataException>(() => DashboardData.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---- pulling ---------------------------------------------------------------------------------------

        [Test]
        public void Pull_adds_unknown_rows_and_updates_untouched_entries()
        {
            var data = Connected();
            data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(1, "a", 0, "Old")));

            var result = data.MergeRemote(new JArray(ServerRow(1, "a", 0, "Server title"), ServerRow(2, "b", 1)));

            Assert.AreEqual(1, result.Added);
            Assert.AreEqual(1, result.Updated);
            Assert.AreEqual("Server title", data.Achievements.First(a => a.Id == 1).Title);
            Assert.AreEqual(DashboardSyncState.Synced, data.Achievements.First(a => a.Id == 1).State);
        }

        [Test]
        public void Pull_keeps_local_edits_instead_of_overwriting_them()
        {
            var data = Connected();
            var local = DashboardAchievement.FromRow(ServerRow(1, "a", 0, "Old"));
            local.Title = "My unsent edit";
            data.Achievements.Add(local);

            var result = data.MergeRemote(new JArray(ServerRow(1, "a", 0, "Server title")));

            Assert.AreEqual(1, result.LocalEditsKept);
            Assert.AreEqual("My unsent edit", local.Title);
            Assert.AreEqual(DashboardSyncState.Modified, local.State);
        }

        [Test]
        public void Pull_leaves_never_pushed_entries_alone()
        {
            var data = Connected();
            var draft = data.AddNew();

            data.MergeRemote(new JArray(ServerRow(1, "a", 0)));

            Assert.Contains(draft, data.Achievements);
            Assert.AreEqual(DashboardSyncState.New, draft.State);
        }

        // ---- manifest --------------------------------------------------------------------------------------

        [Test]
        public void The_manifest_contains_only_entries_that_have_a_server_id_and_the_catalog_version()
        {
            var data = Connected();
            data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(10, "b", 1)));
            data.Achievements.Add(DashboardAchievement.FromRow(ServerRow(11, "a", 0)));
            data.AddNew();

            var manifest = data.BuildManifest();

            Assert.AreEqual(3, manifest.Value<int>("catalogVersion"));
            Assert.AreEqual(7, manifest.Value<long>("gameId"));
            Assert.AreEqual("test-game", manifest.Value<string>("game"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, ((JArray)manifest["achievements"]).Select(t => t.Value<string>("key")));
        }

        [Test]
        public void The_manifest_is_readable_by_the_runtime_catalog()
        {
            var data = Connected();
            var a = DashboardAchievement.FromRow(ServerRow(10, "first_blood", 0, "First Blood"));
            a.Hidden = true;
            a.IconPath = "test-game/first_blood.png";
            a.LocalizationTable = "ST_Achievements";
            a.TitleKey = "first_blood_title";
            data.Achievements.Add(a);

            var catalog = AchievementCatalog.FromJson(DashboardData.SerializeManifest(data.BuildManifest()));

            Assert.IsTrue(catalog.TryGetByKey("first_blood", out var definition));
            Assert.AreEqual(10, definition.Id);
            Assert.IsTrue(definition.Hidden);
            Assert.AreEqual("test-game/first_blood", definition.IconPath, "local icon paths lose their extension");
            Assert.AreEqual("first_blood_title", definition.TitleKey);
            Assert.AreEqual(3, catalog.CatalogVersion);
        }

        // ---- validation ------------------------------------------------------------------------------------

        private static DashboardAchievement Valid(string key = "first", int bit = 0) =>
            new DashboardAchievement { Key = key, BitIndex = bit, Title = "Title", Description = "Description" };

        private static System.Collections.Generic.List<string> Errors(DashboardAchievement a, params DashboardAchievement[] others) =>
            DashboardValidator.Validate(a, new[] { a }.Concat(others).ToList());

        [Test]
        public void A_valid_entry_has_no_errors() => Assert.IsEmpty(Errors(Valid()));

        [TestCase("")]
        [TestCase("has space")]
        [TestCase("_leading")]
        [TestCase("trailing-")]
        [TestCase("ünicode")]
        public void Keys_the_database_would_reject_are_flagged(string key)
        {
            Assert.IsNotEmpty(Errors(Valid(key)));
        }

        [TestCase("a")]
        [TestCase("first_blood")]
        [TestCase("Boss.1-kill")]
        public void Keys_the_database_accepts_pass(string key)
        {
            Assert.IsEmpty(Errors(Valid(key)));
        }

        [Test]
        public void A_key_of_65_characters_is_too_long()
        {
            Assert.IsNotEmpty(Errors(Valid(new string('a', 65))));
            Assert.IsEmpty(Errors(Valid(new string('a', 64))));
        }

        [Test]
        public void Duplicate_keys_and_bit_indexes_are_flagged_on_both_entries()
        {
            var a = Valid("same", 1);
            var b = Valid("same", 1);

            Assert.IsNotEmpty(Errors(a, b));
            Assert.IsNotEmpty(Errors(b, a));
        }

        [TestCase(-1)]
        [TestCase(4096)]
        public void Bit_indexes_outside_the_range_are_flagged(int bit) => Assert.IsNotEmpty(Errors(Valid("k", bit)));

        [Test]
        public void The_bit_index_range_endpoints_are_valid()
        {
            Assert.IsEmpty(Errors(Valid("a", 0)));
            Assert.IsEmpty(Errors(Valid("b", 4095)));
        }

        [Test]
        public void Title_and_description_lengths_follow_the_database_checks()
        {
            var noTitle = Valid();
            noTitle.Title = "";
            Assert.IsNotEmpty(Errors(noTitle));

            var longTitle = Valid();
            longTitle.Title = new string('x', 201);
            Assert.IsNotEmpty(Errors(longTitle));

            var emptyDescription = Valid();
            emptyDescription.Description = "";
            Assert.IsEmpty(Errors(emptyDescription), "description may be empty, it only may not be null");

            var longDescription = Valid();
            longDescription.Description = new string('x', 1001);
            Assert.IsNotEmpty(Errors(longDescription));
        }

        [Test]
        public void Localization_keys_need_a_table()
        {
            var a = Valid();
            a.TitleKey = "t";
            Assert.IsNotEmpty(Errors(a));

            a.LocalizationTable = "ST_Achievements";
            Assert.IsEmpty(Errors(a));
        }

        [TestCase("https://cdn.example.com/a.png", true)]
        [TestCase("http://cdn.example.com/a.png", true)]
        [TestCase("www.example.com/a.png", true)]
        [TestCase("file:///c:/a.png", false)]
        [TestCase("cdn.example.com/a.png", false)]
        public void Icon_urls_must_be_web_addresses(string url, bool valid)
        {
            var a = Valid();
            a.IconUrl = url;
            Assert.AreEqual(valid, !Errors(a).Any());
        }

        [Test]
        public void Display_order_must_fit_a_smallint()
        {
            var a = Valid();
            a.DisplayOrder = 32768;
            Assert.IsNotEmpty(Errors(a));
            a.DisplayOrder = -32768;
            Assert.IsEmpty(Errors(a));
        }

        [TestCase("my-game", true)]
        [TestCase("game1", true)]
        [TestCase("My-Game", false)]
        [TestCase("-game", false)]
        [TestCase("game-", false)]
        [TestCase("", false)]
        public void Game_slugs_follow_the_games_table_check(string slug, bool valid)
        {
            Assert.AreEqual(valid, DashboardValidator.ValidateGame(slug, "Name") == null);
        }
    }
}
