using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DryreLHub.SupabaseGameAchievements.Unity.Tests
{
    /// <summary>PlayMode tests for the Unity overlay. Run from the Unity Test Runner (PlayMode tab).</summary>
    public class UnityAchievementOverlayTests
    {
        private sealed class NullIcons : IAchievementIconProvider
        {
            public Sprite GetIcon(AchievementDefinition definition, out bool isFinal)
            {
                isFinal = true;
                return null;
            }

            public Task<Sprite> GetIconAsync(AchievementDefinition definition, CancellationToken cancellationToken) => Task.FromResult<Sprite>(null);
        }

        private sealed class PendingLocalization : IAchievementLocalizationProvider
        {
            public readonly TaskCompletionSource<AchievementText> Result = new TaskCompletionSource<AchievementText>();

            public bool TryGetText(AchievementDefinition definition, out AchievementText text)
            {
                text = default;
                return false;
            }

            public Task<AchievementText> GetTextAsync(AchievementDefinition definition, CancellationToken cancellationToken) => Result.Task;
        }

        private GameObject _host;
        private UnityAchievementOverlay _overlay;
        private FixedAchievementNotificationSettings _settings;
        private AchievementSystem _system;
        private string _storageDirectory;

        private void Build(IAchievementLocalizationProvider localization = null, AudioClip sound = null)
        {
            var definitions = new List<AchievementDefinition>();
            for (int bit = 0; bit < 4; bit++)
                definitions.Add(new AchievementDefinition(100 + bit, "a" + bit, bit, "Title " + bit, "Description " + bit, iconPath: "missing/icon_" + bit));

            _storageDirectory = Path.Combine(Application.temporaryCachePath, "overlay-tests-" + Guid.NewGuid().ToString("N"));
            _settings = new FixedAchievementNotificationSettings(true);
            _system = new AchievementSystem(new AchievementSystemOptions
            {
                Catalog = new AchievementCatalog(1, "overlay-test", 1, definitions),
                Storage = new FileAchievementStorage(_storageDirectory),
                NotificationSettings = _settings,
                Localization = localization ?? DefaultAchievementLocalizationProvider.Instance,
                BackgroundWrites = false,
            });

            _host = new GameObject("OverlayTestHost");
            _overlay = _host.AddComponent<UnityAchievementOverlay>();
            _overlay.SetTimings(0.05f, 0.1f, 0.05f, 0.05f);
            _overlay.UnlockSound = sound;
            _overlay.Bind(_system.Notifications, new NullIcons());
        }

        [TearDown]
        public void TearDown()
        {
            _system?.Dispose();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            try { if (_storageDirectory != null) Directory.Delete(_storageDirectory, true); } catch (Exception) { }
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds = 5f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("Timed out waiting for condition.");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Queued_toasts_show_one_at_a_time_in_order()
        {
            Build();
            _system.TryUnlock("a0");
            _system.TryUnlock("a1");
            _system.TryUnlock("a2");

            yield return WaitUntil(() => _overlay.ShownCount == 1);
            Assert.AreEqual("Title 0", _overlay.View.TitleText);
            Assert.AreEqual(2, _system.Notifications.Queue.Count, "later toasts wait instead of overlapping");

            yield return WaitUntil(() => _overlay.ShownCount == 2);
            Assert.AreEqual("Title 1", _overlay.View.TitleText);
            yield return WaitUntil(() => _overlay.ShownCount == 3 && !_overlay.IsShowing);
            Assert.AreEqual(1, _host.GetComponentsInChildren<AchievementToastView>(true).Length, "one pooled view is reused");
        }

        [UnityTest]
        public IEnumerator Repeated_unlock_shows_once_and_plays_the_sound_once()
        {
            var clip = AudioClip.Create("beep", 441, 1, 44100, false);
            Build(sound: clip);
            _system.TryUnlock("a0");
            _system.TryUnlock("a0");
            _system.TryUnlock("a0");

            yield return WaitUntil(() => _overlay.ShownCount == 1 && !_overlay.IsShowing);
            for (int i = 0; i < 10; i++) yield return null;
            Assert.AreEqual(1, _overlay.ShownCount);
            Assert.AreEqual(1, _overlay.SoundPlayCount);
        }

        [UnityTest]
        public IEnumerator Disabled_setting_shows_nothing_but_still_unlocks()
        {
            Build();
            _settings.NotificationsEnabled = false;
            Assert.IsTrue(_system.TryUnlock("a0"));
            for (int i = 0; i < 20; i++) yield return null;

            Assert.AreEqual(0, _overlay.ShownCount);
            Assert.IsTrue(_system.HasUnlocked("a0"));
        }

        [UnityTest]
        public IEnumerator Setting_turned_off_while_queued_drops_the_toast()
        {
            Build();
            _system.TryUnlock("a0");
            _settings.NotificationsEnabled = false;
            for (int i = 0; i < 20; i++) yield return null;
            Assert.AreEqual(0, _overlay.ShownCount);
        }

        private static Sprite SolidSprite() => Sprite.Create(new Texture2D(4, 4), new Rect(0, 0, 4, 4), Vector2.one * 0.5f);

        [UnityTest]
        public IEnumerator Layered_icon_style_shows_the_shared_background_behind_a_smaller_icon()
        {
            Build();
            var background = SolidSprite();
            _overlay.ConfigureIconStyle(AchievementIconStyle.Layered, background, 0.2f);
            _system.TryUnlock("a0");

            yield return WaitUntil(() => _overlay.ShownCount == 1);

            Assert.AreEqual(AchievementIconStyle.Layered, _overlay.IconStyle);
            Assert.IsNotNull(_overlay.View.IconBackground);
            Assert.AreSame(background, _overlay.View.IconBackground.sprite);
            Assert.Less(_overlay.View.IconRect.sizeDelta.x, _overlay.View.IconBackground.rectTransform.sizeDelta.x, "the icon sits inside the background");
        }

        [UnityTest]
        public IEnumerator Combined_icon_style_is_the_default_and_uses_no_background()
        {
            Build();
            _system.TryUnlock("a0");

            yield return WaitUntil(() => _overlay.ShownCount == 1);

            Assert.AreEqual(AchievementIconStyle.Combined, _overlay.IconStyle);
            Assert.IsNull(_overlay.View.IconBackground);
        }

        [UnityTest]
        public IEnumerator Layered_without_a_background_stays_combined_and_switching_back_restores_the_icon_size()
        {
            Build();
            _overlay.ConfigureIconStyle(AchievementIconStyle.Layered, null, 0.2f);
            Assert.AreEqual(AchievementIconStyle.Combined, _overlay.IconStyle, "layered needs a background to draw");

            _system.TryUnlock("a0");
            yield return WaitUntil(() => _overlay.ShownCount == 1);
            var combinedSize = _overlay.View.IconRect.sizeDelta;

            _overlay.ConfigureIconStyle(AchievementIconStyle.Layered, SolidSprite(), 0.2f);
            Assert.Less(_overlay.View.IconRect.sizeDelta.x, combinedSize.x);

            _overlay.ConfigureIconStyle(AchievementIconStyle.Combined, null, 0.2f);
            Assert.AreEqual(combinedSize, _overlay.View.IconRect.sizeDelta);
            Assert.IsNull(_overlay.View.IconBackground);
        }

        [UnityTest]
        public IEnumerator Missing_icon_falls_back_to_the_default_placeholder_icon()
        {
            Build();
            LogAssert.ignoreFailingMessages = true;
            _overlay.Bind(_system.Notifications, new ResourcesAchievementIconProvider());
            _system.TryUnlock("a1");

            yield return WaitUntil(() => _overlay.ShownCount == 1);
            Assert.IsTrue(_overlay.View.HasIcon, "a placeholder is shown rather than leaving a blank gap");
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), _overlay.View.Icon);
            Assert.AreEqual("Title 1", _overlay.View.TitleText);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator Missing_icon_uses_a_configured_fallback_over_the_default_placeholder()
        {
            Build();
            var customFallback = Sprite.Create(new Texture2D(1, 1), new Rect(0, 0, 1, 1), Vector2.one * 0.5f);
            LogAssert.ignoreFailingMessages = true;
            _overlay.Bind(_system.Notifications, new ResourcesAchievementIconProvider(fallback: customFallback));
            _system.TryUnlock("a1");

            yield return WaitUntil(() => _overlay.ShownCount == 1);
            Assert.AreSame(customFallback, _overlay.View.Icon);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator An_achievement_added_after_this_build_shipped_still_shows_a_placeholder_icon()
        {
            // Simulates an old build receiving a newer catalog (e.g. via Remote Config) that references an
            // achievement whose icon was never packaged with this build.
            Build();
            var newerDefinitions = new List<AchievementDefinition>
            {
                new AchievementDefinition(500, "added_later", 0, "Added Later", "Shipped after this build.", iconPath: "achievements/added_later"),
            };
            var newerCatalog = new AchievementCatalog(1, "overlay-test", 2, newerDefinitions);
            _overlay.Bind(_system.Notifications, new ResourcesAchievementIconProvider());

            LogAssert.ignoreFailingMessages = true;
            var notification = _system.Notifications.OnUnlocked(new AchievementUnlockedEvent(newerCatalog.Achievements[0], DateTime.UtcNow));
            Assert.IsNotNull(notification);

            yield return WaitUntil(() => _overlay.ShownCount == 1);
            Assert.IsTrue(_overlay.View.HasIcon);
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), _overlay.View.Icon);
            LogAssert.ignoreFailingMessages = false;
        }

        [UnityTest]
        public IEnumerator Late_localization_replaces_fallback_text_while_visible()
        {
            var localization = new PendingLocalization();
            Build(localization);
            _overlay.SetTimings(0.05f, 2f, 0.05f, 0.01f);
            _system.TryUnlock("a2");

            yield return WaitUntil(() => _overlay.ShownCount == 1);
            Assert.AreEqual("Title 2", _overlay.View.TitleText, "fallback shown after the grace period");

            localization.Result.SetResult(new AchievementText("Titel 2", "Beschreibung 2"));
            yield return WaitUntil(() => _overlay.View.TitleText == "Titel 2");
            Assert.AreEqual("Beschreibung 2", _overlay.View.DescriptionText);
        }
    }
}

