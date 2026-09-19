using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class ResourcesAchievementIconProviderTests
    {
        private static AchievementDefinition Definition(long id, string key, string iconPath) =>
            new AchievementDefinition(id, key, (int)id, "Title " + key, "Description " + key, iconPath: iconPath);

        private static AchievementDefinition DefinitionWithUrl(long id, string key, string iconPath, string iconUrl) =>
            new AchievementDefinition(id, key, (int)id, "Title " + key, "Description " + key, iconPath: iconPath, iconUrl: iconUrl);

        private static Sprite MakeSprite(string name)
        {
            var texture = new Texture2D(1, 1);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), Vector2.one * 0.5f);
            sprite.name = name;
            return sprite;
        }

        [Test]
        public void Own_icon_found_locally_is_returned_and_final()
        {
            var real = MakeSprite("real");
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: path => path == "Achievements/g/a" ? real : null);

            var icon = provider.GetIcon(Definition(1, "a", "g/a"), out bool isFinal);

            Assert.AreSame(real, icon);
            Assert.IsTrue(isFinal);
        }

        [Test]
        public void Missing_icon_with_no_fallback_configured_and_no_game_fallback_file_uses_generated_default()
        {
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: _ => null);

            var icon = provider.GetIcon(Definition(1, "a", "g/a"), out bool isFinal);

            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), icon);
            Assert.IsTrue(isFinal);
        }

        [Test]
        public void Missing_icon_uses_the_explicit_constructor_fallback_over_the_game_folder_convention()
        {
            var explicitFallback = MakeSprite("explicit");
            var gameFallback = MakeSprite("game-folder");
            var provider = new ResourcesAchievementIconProvider("Achievements/", fallback: explicitFallback,
                localLoader: path => path == "Achievements/g/fallback" ? gameFallback : null);

            var icon = provider.GetIcon(Definition(1, "a", "g/a"), out bool isFinal);

            Assert.AreSame(explicitFallback, icon, "an explicitly configured fallback always wins");
            Assert.IsTrue(isFinal);
        }

        [Test]
        public void Missing_icon_falls_back_to_a_fallback_png_next_to_the_other_icons_in_the_same_folder()
        {
            var gameFallback = MakeSprite("game-folder");
            var requested = new List<string>();
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: path =>
            {
                requested.Add(path);
                return path == "Achievements/hellasure/fallback" ? gameFallback : null;
            });

            var icon = provider.GetIcon(Definition(1, "lava_walker", "hellasure/lava_walker"), out bool isFinal);

            Assert.AreSame(gameFallback, icon);
            Assert.IsTrue(isFinal);
            CollectionAssert.Contains(requested, "Achievements/hellasure/fallback");
        }

        [Test]
        public void Game_folder_fallback_convention_works_at_the_prefix_root_when_the_icon_path_has_no_subfolder()
        {
            var gameFallback = MakeSprite("root-fallback");
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: path =>
                path == "Achievements/fallback" ? gameFallback : null);

            var icon = provider.GetIcon(Definition(1, "a", "a"), out _);

            Assert.AreSame(gameFallback, icon);
        }

        [Test]
        public void Game_folder_fallback_is_only_loaded_once_and_cached_across_achievements_in_the_same_folder()
        {
            int loadCalls = 0;
            var gameFallback = MakeSprite("game-folder");
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: path =>
            {
                if (path == "Achievements/hellasure/fallback") { loadCalls++; return gameFallback; }
                return null; // every achievement's own icon is "missing"
            });

            provider.GetIcon(Definition(1, "a", "hellasure/a"), out _);
            provider.GetIcon(Definition(2, "b", "hellasure/b"), out _);

            Assert.AreEqual(1, loadCalls, "the game-folder fallback sprite is loaded once, then reused");
        }

        [Test]
        public void Result_is_cached_per_achievement_after_the_first_resolution()
        {
            int loadCalls = 0;
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: _ => { loadCalls++; return null; });
            var definition = Definition(1, "a", "g/a");

            provider.GetIcon(definition, out _);
            int callsAfterFirstResolution = loadCalls; // own icon + game-folder fallback, both miss

            provider.GetIcon(definition, out _);
            provider.GetIcon(definition, out _);

            Assert.AreEqual(callsAfterFirstResolution, loadCalls, "later calls hit the per-achievement cache and touch the loader no further");
        }

        [Test]
        public void A_throwing_local_loader_does_not_propagate_and_still_yields_a_fallback()
        {
            var provider = new ResourcesAchievementIconProvider("Achievements/", localLoader: _ => throw new InvalidOperationException("boom"));

            Sprite icon = null;
            Assert.DoesNotThrow(() => icon = provider.GetIcon(Definition(1, "a", "g/a"), out _));
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), icon);
        }

        [TestCase("https://cdn.example.com/icons/a.webp")]
        [TestCase("http://cdn.example.com/icons/a.webp")]
        [TestCase("www.example.com/icons/a.webp")]
        public void A_url_icon_path_shows_a_fallback_immediately_and_is_not_final(string iconPath)
        {
            var provider = new ResourcesAchievementIconProvider("Achievements/",
                localLoader: _ => null,
                urlDownloader: _ => new TaskCompletionSource<Sprite>().Task); // never completes during this test

            var icon = provider.GetIcon(Definition(1, "a", iconPath), out bool isFinal);

            Assert.IsFalse(isFinal);
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), icon, "shows the fallback chain immediately, never blocks on the network");
        }

        [Test]
        public void Www_prefixed_icon_path_is_normalized_to_https_before_downloading()
        {
            string requestedUrl = null;
            var provider = new ResourcesAchievementIconProvider(urlDownloader: url =>
            {
                requestedUrl = url;
                return Task.FromResult<Sprite>(null);
            });

            provider.GetIcon(Definition(1, "a", "www.example.com/a.png"), out _);

            Assert.AreEqual("https://www.example.com/a.png", requestedUrl);
        }

        [Test]
        public async Task GetIconAsync_resolves_to_the_downloaded_sprite_once_the_download_completes()
        {
            var downloaded = MakeSprite("downloaded");
            var completion = new TaskCompletionSource<Sprite>();
            var provider = new ResourcesAchievementIconProvider(urlDownloader: _ => completion.Task);
            var definition = Definition(1, "a", "https://cdn.example.com/a.png");

            provider.GetIcon(definition, out bool isFinal);
            Assert.IsFalse(isFinal);

            completion.SetResult(downloaded);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(downloaded, resolved);
        }

        [Test]
        public async Task GetIconAsync_falls_back_when_the_download_fails()
        {
            var provider = new ResourcesAchievementIconProvider(urlDownloader: _ => Task.FromResult<Sprite>(null));
            var definition = Definition(1, "a", "https://cdn.example.com/a.png");

            provider.GetIcon(definition, out _);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), resolved);
        }

        [Test]
        public async Task After_GetIconAsync_resolves_a_later_GetIcon_call_returns_the_final_sprite_synchronously()
        {
            var downloaded = MakeSprite("downloaded");
            var provider = new ResourcesAchievementIconProvider(urlDownloader: _ => Task.FromResult(downloaded));
            var definition = Definition(1, "a", "https://cdn.example.com/a.png");

            provider.GetIcon(definition, out _);
            await provider.GetIconAsync(definition, CancellationToken.None);
            var icon = provider.GetIcon(definition, out bool isFinal);

            Assert.AreSame(downloaded, icon);
            Assert.IsTrue(isFinal);
        }

        [Test]
        public void Two_achievements_sharing_the_same_url_only_trigger_one_download()
        {
            int downloadCalls = 0;
            var provider = new ResourcesAchievementIconProvider(urlDownloader: _ =>
            {
                downloadCalls++;
                return new TaskCompletionSource<Sprite>().Task;
            });

            provider.GetIcon(Definition(1, "a", "https://cdn.example.com/shared.png"), out _);
            provider.GetIcon(Definition(2, "b", "https://cdn.example.com/shared.png"), out _);

            Assert.AreEqual(1, downloadCalls);
        }

        [Test]
        public void Null_definition_returns_a_fallback_without_throwing()
        {
            var provider = new ResourcesAchievementIconProvider();
            Sprite icon = null;
            Assert.DoesNotThrow(() => icon = provider.GetIcon(null, out _));
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), icon);
        }
        // ======================================================================
        // icon_url tests
        // ======================================================================

        [Test]
        public void Icon_url_shows_a_fallback_immediately_and_is_not_final()
        {
            var provider = new ResourcesAchievementIconProvider(
                urlDownloader: _ => new TaskCompletionSource<Sprite>().Task); // never completes

            var icon = provider.GetIcon(DefinitionWithUrl(1, "a", null, "https://cdn.example.com/a.png"), out bool isFinal);

            Assert.IsFalse(isFinal);
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), icon,
                "icon_url triggers async download; returns fallback immediately without blocking");
        }

        [Test]
        public async Task Icon_url_download_success_resolves_to_the_downloaded_sprite()
        {
            var downloaded = MakeSprite("from-icon-url");
            var provider = new ResourcesAchievementIconProvider(
                urlDownloader: _ => Task.FromResult(downloaded));
            var definition = DefinitionWithUrl(1, "a", null, "https://cdn.example.com/a.png");

            provider.GetIcon(definition, out _);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(downloaded, resolved);
        }

        [Test]
        public async Task Icon_url_download_failure_falls_back_to_icon_path_local()
        {
            var localSprite = MakeSprite("local-icon-path");
            var provider = new ResourcesAchievementIconProvider(
                localLoader: path => path == "hellasure/first_blood" ? localSprite : null,
                urlDownloader: _ => Task.FromResult<Sprite>(null)); // simulate failure
            var definition = DefinitionWithUrl(1, "first_blood", "hellasure/first_blood", "https://cdn.example.com/broken.png");

            provider.GetIcon(definition, out _);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(localSprite, resolved,
                "when icon_url download fails the runtime must fall through to icon_path");
        }

        [Test]
        public async Task Icon_url_and_icon_path_both_fail_falls_back_to_generated_default()
        {
            var provider = new ResourcesAchievementIconProvider(
                localLoader: _ => null,
                urlDownloader: _ => Task.FromResult<Sprite>(null));
            var definition = DefinitionWithUrl(1, "a", "g/a", "https://cdn.example.com/broken.png");

            provider.GetIcon(definition, out _);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), resolved);
        }

        [Test]
        public void Icon_url_takes_priority_over_icon_path_when_both_are_set()
        {
            // GetIcon is sync; when icon_url is set it must be the one that triggers the download,
            // not icon_path (even though icon_path is also a URL in this case).
            int downloadCalls = 0;
            string downloadedUrl = null;
            var provider = new ResourcesAchievementIconProvider(
                urlDownloader: url =>
                {
                    downloadCalls++;
                    downloadedUrl = url;
                    return new TaskCompletionSource<Sprite>().Task;
                });
            var definition = DefinitionWithUrl(1, "a",
                "https://cdn.example.com/path-url.png",
                "https://cdn.example.com/icon-url.png");

            provider.GetIcon(definition, out bool isFinal);

            Assert.IsFalse(isFinal);
            Assert.AreEqual(1, downloadCalls, "only one download should start on the first GetIcon call");
            Assert.AreEqual("https://cdn.example.com/icon-url.png", downloadedUrl,
                "icon_url must be downloaded, not icon_path");
        }

        [Test]
        public async Task Icon_url_failure_with_url_in_icon_path_downloads_icon_path_url_as_fallback()
        {
            var pathUrlSprite = MakeSprite("path-url");
            string lastDownloaded = null;
            var provider = new ResourcesAchievementIconProvider(
                urlDownloader: url =>
                {
                    lastDownloaded = url;
                    // First call (icon_url) fails; second call (icon_path URL) succeeds.
                    return url.Contains("icon-url") ? Task.FromResult<Sprite>(null) : Task.FromResult(pathUrlSprite);
                });
            var definition = DefinitionWithUrl(1, "a",
                "https://cdn.example.com/path-url.png",
                "https://cdn.example.com/icon-url.png");

            provider.GetIcon(definition, out _);
            var resolved = await provider.GetIconAsync(definition, CancellationToken.None);

            Assert.AreSame(pathUrlSprite, resolved,
                "when icon_url fails and icon_path is a URL, that URL should be downloaded as fallback");
        }

        [Test]
        public async Task After_icon_url_resolves_later_GetIcon_returns_the_final_sprite_synchronously()
        {
            var downloaded = MakeSprite("cached");
            var provider = new ResourcesAchievementIconProvider(
                urlDownloader: _ => Task.FromResult(downloaded));
            var definition = DefinitionWithUrl(1, "a", null, "https://cdn.example.com/a.png");

            provider.GetIcon(definition, out _);
            await provider.GetIconAsync(definition, CancellationToken.None);

            var icon = provider.GetIcon(definition, out bool isFinal);
            Assert.AreSame(downloaded, icon);
            Assert.IsTrue(isFinal);
        }
    }
}
