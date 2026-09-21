using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// The Overlay tab: restyle the achievement toast (colors, fonts, position, motion, timing, sound, header text) with a
    /// live preview, or build a toast of your own as a prefab with a step-by-step guide and a checker. Everything is saved to
    /// <c>Resources/Achievements/overlay.json</c>, which the game loads by itself. See <see cref="AchievementOverlaySettings"/>.
    /// </summary>
    public sealed partial class AchievementDashboardWindow
    {
        private enum OverlayPage { Customize, BuildYourOwn }

        private static readonly string[] PageLabels = { "Customize the toast", "Build your own toast (tutorial)" };
        private static readonly string[] CornerLabels = { "Bottom right", "Bottom left", "Top right", "Top left", "Bottom center", "Top center" };
        private static readonly string[] AnimationLabels = { "Slide in from the edge", "Fade", "Pop (grow while fading in)" };

        private OverlayPage _overlayPage;
        private AchievementOverlaySettings _overlay = AchievementOverlaySettings.CreateDefault();
        private bool _overlayLoaded;
        private bool _overlayFileExists;
        [NonSerialized] private string _overlayLoadError;
        [NonSerialized] private string _overlaySaveError;
        private bool _overlayDirty;
        private double _overlaySaveAt;
        private Vector2 _overlayScroll;
        [NonSerialized] private string _overlayNote;
        private double _previewStart = -1;
        private GameObject _toastPrefabAsset;
        private List<ToastCheck> _toastChecks = new List<ToastCheck>();
        private string _toastChecksFor;

        // =====================================================================================================
        // File
        // =====================================================================================================

        private void LoadOverlayFile()
        {
            _overlayLoaded = true;
            if (_overlayDirty) return; // never replace edits that have not reached the disk

            _overlayLoadError = null;
            string full = ToFullPath(AchievementOverlayFile.DefaultPath);
            _overlayFileExists = File.Exists(full);
            if (!_overlayFileExists)
            {
                _overlay = AchievementOverlaySettings.CreateDefault();
                return;
            }

            try
            {
                string text = File.ReadAllText(full);
                if (AchievementOverlaySettings.TryParse(text, out var parsed)) _overlay = parsed;
                else _overlayLoadError = "overlay.json could not be read. Fix it, or delete it to start again from the defaults. Nothing is saved over it until then.";
            }
            catch (Exception e)
            {
                _overlayLoadError = "overlay.json could not be read: " + DashboardData.Describe(e);
            }
        }

        private void OverlayChanged()
        {
            _overlay.Sanitize();
            _overlayDirty = true;
            _overlaySaveAt = EditorApplication.timeSinceStartup + 0.4;
        }

        private void SaveOverlayIfDue()
        {
            if (_overlayDirty && EditorApplication.timeSinceStartup >= _overlaySaveAt) SaveOverlayNow();
        }

        private void FlushOverlay()
        {
            if (_overlayDirty) SaveOverlayNow();
        }

        private void SaveOverlayNow()
        {
            if (!string.IsNullOrEmpty(_overlayLoadError)) { _overlayDirty = false; return; }

            try
            {
                string path = AchievementOverlayFile.DefaultPath;
                AchievementToastPrefabTools.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                bool wrote = AchievementOverlayFile.Write(ToFullPath(path), _overlay.ToJson());
                if (wrote) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                _overlayFileExists = true;
                _overlaySaveError = null;
                _overlayDirty = false;
                ApplyOverlayToRunningGame();
            }
            catch (Exception e)
            {
                string described = DashboardData.Describe(e);
                if (string.IsNullOrEmpty(_overlaySaveError)) Debug.LogError("[Achievements] Could not save overlay.json:\n" + e);
                _overlaySaveError = "Could not save overlay.json: " + described;
                _overlaySaveAt = EditorApplication.timeSinceStartup + 1.0; // try again
            }
        }

        private static UnityAchievementOverlay RunningOverlay =>
            Application.isPlaying && UnityAchievementManager.Instance != null ? UnityAchievementManager.Instance.Overlay : null;

        private void ApplyOverlayToRunningGame()
        {
            var overlay = RunningOverlay;
            if (overlay != null) overlay.ApplySettings(_overlay);
        }

        // =====================================================================================================
        // Tab
        // =====================================================================================================

        private void DrawOverlayTab()
        {
            if (!_overlayLoaded) LoadOverlayFile();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                _overlayPage = (OverlayPage)GUILayout.Toolbar((int)_overlayPage, PageLabels, GUILayout.Height(22));
                GUILayout.Space(8);
            }

            if (!string.IsNullOrEmpty(_overlayLoadError)) DrawErrorBox(_overlayLoadError);
            if (!string.IsNullOrEmpty(_overlaySaveError)) DrawErrorBox(_overlaySaveError);

            using (var scroll = new EditorGUILayout.ScrollViewScope(_overlayScroll))
            {
                _overlayScroll = scroll.scrollPosition;
                if (_overlayPage == OverlayPage.Customize) DrawCustomizePage();
                else DrawTutorialPage();
                EditorGUILayout.Space(12);
            }
        }

        // =====================================================================================================
        // Page 1: customize
        // =====================================================================================================

        private void DrawCustomizePage()
        {
            DrawOverlayFileLine();
            DrawPreview();
            DrawLiveControls();

            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(_overlayLoadError)))
            {
                EditorGUI.BeginChangeCheck();

                DrawSection("Text", () =>
                {
                    _overlay.headerText = HintText("Header text", "The small line above the achievement's title. Empty = 'ACHIEVEMENT UNLOCKED' in the game's language (English, Turkish, Spanish, French, Russian).",
                        _overlay.headerText, "ACHIEVEMENT UNLOCKED (automatic, per language)");
                    _overlay.headerLocalizationTable = EditorGUILayout.TextField(new GUIContent("Header table", "Optional Unity Localization string table to read the header from."), _overlay.headerLocalizationTable);
                    _overlay.headerLocalizationKey = EditorGUILayout.TextField(new GUIContent("Header key", "Entry in that table. When it exists it wins over the text above."), _overlay.headerLocalizationKey);
                });

                DrawSection("Colors  (built-in toast)", () =>
                {
                    _overlay.backgroundColor = ColorField("Background (top-left)", "Start of the diagonal background gradient. Lower the alpha for a see-through toast.", _overlay.backgroundColor);
                    _overlay.backgroundColor2 = ColorField("Background (bottom-right)", "End of the gradient. Same as the first color for a flat background.", _overlay.backgroundColor2);
                    _overlay.headerColor = ColorField("Header text", null, _overlay.headerColor);
                    _overlay.titleColor = ColorField("Title text", null, _overlay.titleColor);
                    _overlay.descriptionColor = ColorField("Description text", null, _overlay.descriptionColor);
                    _overlay.cornerRadius = EditorGUILayout.IntSlider(new GUIContent("Corner radius", "0 = square corners."), _overlay.cornerRadius, 0, 30);
                    _overlay.shadowEnabled = EditorGUILayout.Toggle("Shadow", _overlay.shadowEnabled);
                    using (new EditorGUI.DisabledScope(!_overlay.shadowEnabled))
                    {
                        _overlay.shadowColor = ColorField("Shadow color", null, _overlay.shadowColor);
                        _overlay.shadowDistance = EditorGUILayout.Slider("Shadow distance", _overlay.shadowDistance, 0f, 40f);
                    }
                });

                DrawSection("Fonts  (built-in toast)", () =>
                {
                    AssetField<Font>("Font", "A Font asset (TTF/OTF) inside a Resources folder. Empty = Unity's default font.", ref _overlay.fontResource, AchievementOverlayFile.FontFolder);
                    _overlay.headerFontSize = EditorGUILayout.IntField("Header size", _overlay.headerFontSize);
                    _overlay.titleFontSize = EditorGUILayout.IntField("Title size", _overlay.titleFontSize);
                    _overlay.descriptionFontSize = EditorGUILayout.IntField("Description size", _overlay.descriptionFontSize);
                    EditorGUILayout.LabelField("Long text shrinks to fit, down to 3-6 points smaller than these sizes.", _styles.Mini);
                });

                DrawSection("Position and size", () =>
                {
                    _overlay.corner = (AchievementToastCorner)EditorGUILayout.Popup(new GUIContent("Screen position"), (int)_overlay.corner, CornerLabels);
                    _overlay.marginX = EditorGUILayout.FloatField(new GUIContent("Margin X", "Distance from the screen's side edge, in pixels of the 1920 x 1080 reference canvas."), _overlay.marginX);
                    _overlay.marginY = EditorGUILayout.FloatField(new GUIContent("Margin Y", "Distance from the top or bottom edge."), _overlay.marginY);
                    _overlay.scale = EditorGUILayout.Slider(new GUIContent("Size", "Scales the whole toast. 1 = 440 x 108."), _overlay.scale, 0.25f, 4f);
                    _overlay.sortingOrder = EditorGUILayout.IntField(new GUIContent("Sort order", "Higher draws in front of other Canvases. The default sits above almost everything."), _overlay.sortingOrder);
                });

                DrawSection("Motion and timing  (seconds)", () =>
                {
                    _overlay.animation = (AchievementToastAnimation)EditorGUILayout.Popup(new GUIContent("Animation"), (int)_overlay.animation, AnimationLabels);
                    _overlay.enterDuration = EditorGUILayout.FloatField(new GUIContent("Appear", "How long the toast takes to come in."), _overlay.enterDuration);
                    _overlay.holdDuration = EditorGUILayout.FloatField(new GUIContent("Stay on screen", "How long it stays fully visible."), _overlay.holdDuration);
                    _overlay.exitDuration = EditorGUILayout.FloatField(new GUIContent("Disappear", "How long it takes to leave."), _overlay.exitDuration);
                    _overlay.gapDuration = EditorGUILayout.FloatField(new GUIContent("Gap between toasts", "Pause after one toast leaves before the next queued one appears."), _overlay.gapDuration);
                    _overlay.localizationGrace = EditorGUILayout.FloatField(new GUIContent("Wait for translation", "How long a toast waits for its localized text before showing the untranslated text."), _overlay.localizationGrace);
                    EditorGUILayout.LabelField("Total per toast: " + (_overlay.enterDuration + _overlay.holdDuration + _overlay.exitDuration + _overlay.gapDuration).ToString("0.0#") + " s. Toasts queue, one at a time.", _styles.Mini);
                });

                DrawSection("Sound", () =>
                {
                    AssetField<AudioClip>("Unlock sound", "Played once when a toast appears. An AudioClip inside a Resources folder. Empty = silent.", ref _overlay.soundResource, AchievementOverlayFile.SoundFolder);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_overlay.soundResource)))
                        _overlay.volume = EditorGUILayout.Slider("Volume", _overlay.volume, 0f, 1f);
                });

                DrawSection("Toast used", () =>
                {
                    if (_overlay.UsesCustomPrefab)
                    {
                        EditorGUILayout.LabelField("Your prefab: Resources/" + _overlay.toastPrefabResource, _styles.Mini);
                        EditorGUILayout.LabelField("Colors, fonts, corner radius and shadow above belong to the built-in toast; your prefab has its own. Position, size, animation, timing, sound and header text still apply.", EditorStyles.wordWrappedMiniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("The built-in toast, drawn in code. To use your own design, open 'Build your own toast (tutorial)' above.", EditorStyles.wordWrappedMiniLabel);
                    }
                });

                if (EditorGUI.EndChangeCheck()) OverlayChanged();
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                GUILayout.Label(_overlayFileExists ? "Saved automatically to " + AchievementOverlayFile.DefaultPath : "Nothing saved yet: the toast uses its built-in look until you change something.", _styles.Mini);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset to defaults", EditorStyles.miniButton, GUILayout.Width(110)) &&
                    EditorUtility.DisplayDialog("Reset the toast", "Put every overlay setting back to its default? Your custom prefab (if any) is disconnected but not deleted.", "Reset", "Cancel"))
                {
                    _overlay = AchievementOverlaySettings.CreateDefault();
                    _overlayLoadError = null;
                    OverlayChanged();
                    GUI.FocusControl(null);
                }
                if (_overlayFileExists && GUILayout.Button("Show file", EditorStyles.miniButton, GUILayout.Width(70)))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AchievementOverlayFile.DefaultPath));
                GUILayout.Space(8);
            }
        }

        private void DrawOverlayFileLine()
        {
            if (!string.IsNullOrEmpty(_overlayNote)) EditorGUILayout.HelpBox(_overlayNote, MessageType.Info);
        }

        private void DrawLiveControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                if (GUILayout.Button("Replay animation in the preview", GUILayout.Height(22))) _previewStart = EditorApplication.timeSinceStartup;

                var overlay = RunningOverlay;
                using (new EditorGUI.DisabledScope(overlay == null || !AchievementManager.IsInitialized))
                {
                    if (GUILayout.Button(new GUIContent("Show a test toast in the game", "Play Mode only: shows a toast for the first achievement, exactly as an unlock would, without unlocking it."), GUILayout.Height(22)))
                        ShowTestToast();
                }
                GUILayout.Space(8);
            }

            if (!Application.isPlaying)
                EditorGUILayout.LabelField("Enter Play Mode to see the real toast: every change here is applied to the running game as you make it.", EditorStyles.wordWrappedMiniLabel);
            else if (RunningOverlay == null)
                EditorGUILayout.HelpBox("The achievement system is not running in this scene, so there is no toast to test. Check 'Game setup' on the Achievements tab.", MessageType.Warning);
        }

        private void ShowTestToast()
        {
            if (_overlayDirty) SaveOverlayNow();
            var system = AchievementManager.Current;
            var definition = system.GetDefinitions().FirstOrDefault(d => !d.IsRetired);
            if (definition == null) { _overlayNote = "The catalog has no achievements to show."; return; }

            var queued = system.Notifications?.OnUnlocked(new AchievementUnlockedEvent(definition, DateTime.UtcNow));
            _overlayNote = queued == null ? "The toast was not queued: notifications are switched off for this game." : null;
        }

        private void DrawSection(string title, Action body)
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                EditorGUILayout.LabelField(title, _styles.CardTitle);
                body();
            }
        }

        private static string HintText(string label, string tooltip, string value, string hint)
        {
            var content = new GUIContent(label, tooltip);
            Rect rect = EditorGUILayout.GetControlRect();
            GUI.SetNextControlName("hint-" + label);
            string result = EditorGUI.TextField(rect, content, value);
            if (string.IsNullOrEmpty(result) && GUI.GetNameOfFocusedControl() != "hint-" + label)
            {
                var hintRect = new Rect(rect.x + EditorGUIUtility.labelWidth + 2, rect.y, rect.width - EditorGUIUtility.labelWidth - 2, rect.height);
                var style = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 0.85f) } };
                GUI.Label(hintRect, hint, style);
            }
            return result;
        }

        private static string ColorField(string label, string tooltip, string hex)
        {
            Color current = AchievementOverlaySettings.ColorOrDefault(hex, new Color32(255, 255, 255, 255));
            Color chosen = EditorGUILayout.ColorField(new GUIContent(label, tooltip), current, true, true, false);
            return chosen == current ? hex : AchievementOverlaySettings.ToHex(chosen);
        }

        /// <summary>
        /// An object field for an asset the game loads through Resources. The file stores only the Resources path, so an asset
        /// outside a Resources folder is offered a copy inside <paramref name="folder"/>.
        /// </summary>
        private void AssetField<T>(string label, string tooltip, ref string resourcePath, string folder) where T : UnityEngine.Object
        {
            T current = string.IsNullOrEmpty(resourcePath) ? null : Resources.Load<T>(resourcePath);
            var picked = (T)EditorGUILayout.ObjectField(new GUIContent(label, tooltip), current, typeof(T), false);

            if (picked != current)
            {
                if (picked == null) resourcePath = "";
                else
                {
                    string assetPath = AssetDatabase.GetAssetPath(picked);
                    string path = AchievementResourcePaths.FromAssetPath(assetPath);
                    if (path == null)
                    {
                        if (EditorUtility.DisplayDialog("Copy into Resources?",
                                "'" + picked.name + "' is not inside a Resources folder, so the built game could not load it.\n\nCopy it to " + folder + "/ ?",
                                "Copy", "Cancel"))
                        {
                            AchievementToastPrefabTools.EnsureFolder(folder);
                            string target = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(assetPath));
                            if (AssetDatabase.CopyAsset(assetPath, target)) path = AchievementResourcePaths.FromAssetPath(target);
                        }
                    }
                    if (path != null) resourcePath = path;
                }
            }

            if (!string.IsNullOrEmpty(resourcePath) && current == null && picked == null)
                EditorGUILayout.HelpBox("Nothing was found at Resources/" + resourcePath + ". Pick the asset again, or clear the field.", MessageType.Warning);
        }

        // =====================================================================================================
        // Preview
        // =====================================================================================================

        private const float PreviewAspect = 16f / 9f;

        private void DrawPreview()
        {
            float width = Mathf.Min(position.width - 32f, 620f);
            var area = GUILayoutUtility.GetRect(width, width / PreviewAspect, GUILayout.ExpandWidth(false));
            area.x = (position.width - area.width) * 0.5f;

            float visibility = PreviewVisibility();
            GUI.BeginGroup(area);
            var local = new Rect(0, 0, area.width, area.height);
            EditorGUI.DrawRect(local, new Color(0.13f, 0.14f, 0.17f));
            EditorGUI.DrawRect(new Rect(0, 0, local.width, 1), new Color(1, 1, 1, 0.08f));
            GUI.Label(new Rect(8, 6, 200, 16), "Preview  (1920 x 1080 screen)", _styles.Mini);

            DrawToastMock(local, _overlay, visibility);
            GUI.EndGroup();

            if (visibility < 1f || _previewStart >= 0) Repaint();
        }

        // 0..1 for the replay; 1 (fully shown) when no replay is running.
        private float PreviewVisibility()
        {
            if (_previewStart < 0) return 1f;
            double t = EditorApplication.timeSinceStartup - _previewStart;
            double enter = Math.Max(0.01, _overlay.enterDuration), hold = _overlay.holdDuration, exit = Math.Max(0.01, _overlay.exitDuration);
            if (t < enter) return Ease((float)(t / enter));
            if (t < enter + hold) return 1f;
            if (t < enter + hold + exit) return 1f - Ease((float)((t - enter - hold) / exit));
            if (t < enter + hold + exit + 0.6) return 0f;
            _previewStart = -1; // finished: show the toast fully again
            return 1f;
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }

        /// <summary>Draws the toast the way the built-in view lays it out, scaled to the preview's screen.</summary>
        private void DrawToastMock(Rect screen, AchievementOverlaySettings s, float visibility)
        {
            float k = screen.width / 1920f;
            const float baseW = 440f, baseH = 108f;
            float scale = s.scale;
            if (s.animation == AchievementToastAnimation.Pop) scale *= Mathf.Lerp(0.8f, 1f, visibility);

            Vector2 size = new Vector2(baseW, baseH) * scale * k;
            Vector2 anchor = AchievementToastView.CornerAnchor(s.corner);
            float slide = AchievementToastView.SlideSign(s.corner);
            float xSign = anchor.x > 0.75f ? -1f : anchor.x < 0.25f ? 1f : 0f;

            // Unity UI space (y up), resting spot, then the hidden offset of the slide animation.
            Vector2 pos = new Vector2(xSign * s.marginX, -slide * s.marginY) * k;
            if (s.animation == AchievementToastAnimation.Slide)
                pos.y += slide * (1f - visibility) * (baseH * s.scale + 24f) * k;

            float left = anchor.x * screen.width + pos.x - anchor.x * size.x;
            float bottom = anchor.y * screen.height + pos.y - anchor.y * size.y;
            var panel = new Rect(left, screen.height - bottom - size.y, size.x, size.y);

            GUI.color = new Color(1, 1, 1, visibility);
            float radius = s.cornerRadius * scale * k;

            if (s.UsesCustomPrefab)
            {
                GUI.DrawTexture(panel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.3f, 0.32f, 0.38f, 0.9f), Vector4.zero, new Vector4(radius, radius, radius, radius));
                var centered = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
                GUI.Label(panel, "Your prefab\n" + Path.GetFileName(s.toastPrefabResource), centered);
                GUI.color = Color.white;
                return;
            }

            if (s.shadowEnabled)
            {
                var shadowRect = new Rect(panel.x, panel.y + s.shadowDistance * scale * k, panel.width, panel.height);
                GUI.DrawTexture(shadowRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, s.ShadowColor, Vector4.zero, new Vector4(radius, radius, radius, radius));
            }

            var sprite = RoundedBoxSprite.GetOrCreateGradient(440, 108, s.cornerRadius, s.BackgroundColor, s.BackgroundColor2);
            GUI.DrawTexture(panel, sprite.texture, ScaleMode.StretchToFill, true);

            float u = scale * k;
            var iconSprite = DefaultAchievementIcon.GetOrCreate();
            if (iconSprite != null)
                GUI.DrawTexture(new Rect(panel.x + 20 * u, panel.y + (baseH - 72) * 0.5f * u, 72 * u, 72 * u), iconSprite.texture, ScaleMode.ScaleToFit, true);

            Font font = string.IsNullOrEmpty(s.fontResource) ? null : Resources.Load<Font>(s.fontResource);
            DrawMockText(panel, u, 108, 10, 316, 20, string.IsNullOrEmpty(s.headerText) ? "ACHIEVEMENT UNLOCKED" : s.headerText, s.headerFontSize, FontStyle.Bold, s.HeaderColor, font, visibility);
            DrawMockText(panel, u, 108, 30, 316, 32, "First Steps", s.titleFontSize, FontStyle.Bold, s.TitleColor, font, visibility);
            DrawMockText(panel, u, 108, 64, 316, 36, "Finish the tutorial level.", s.descriptionFontSize, FontStyle.Normal, s.DescriptionColor, font, visibility);
            GUI.color = Color.white;
        }

        private static void DrawMockText(Rect panel, float u, float x, float y, float w, float h, string text, int size, FontStyle style, Color32 color, Font font, float visibility)
        {
            var textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(1, Mathf.RoundToInt(size * u)),
                fontStyle = style,
                wordWrap = true,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0),
                font = font,
            };
            Color c = color;
            c.a *= visibility;
            textStyle.normal.textColor = c;
            GUI.Label(new Rect(panel.x + x * u, panel.y + y * u, w * u, h * u), text, textStyle);
        }

        // =====================================================================================================
        // Page 2: build your own toast
        // =====================================================================================================

        private void DrawTutorialPage()
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                EditorGUILayout.LabelField("Your own toast, from scratch", _styles.CardTitle);
                EditorGUILayout.LabelField(
                    "A toast is a UI Canvas saved as a prefab. When an achievement unlocks, the game makes one copy of it, fills in the title, description and icon, " +
                    "slides its Panel in and out, plays the sound, and queues the next one. Everything about how it looks is yours: the game only needs to know which " +
                    "child is the panel, the icon and the texts. Follow the steps, or press the button to get a finished starter prefab and restyle that.",
                    EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Create a starter prefab for me", "Builds steps 1-6 in one click: a working toast prefab in " + AchievementOverlayFile.PrefabFolder + ", already connected."), GUILayout.Height(26)))
                        CreateStarter();
                    if (GUILayout.Button("Watch the toast in the preview", GUILayout.Height(26), GUILayout.Width(200)))
                    {
                        _overlayPage = OverlayPage.Customize;
                        _previewStart = EditorApplication.timeSinceStartup;
                    }
                }
            }

            Step(1, "Create the Canvas",
                "Hierarchy: right-click, UI, Canvas. Name it AchievementToast.\n" +
                "  • Canvas > Render Mode: Screen Space - Overlay.\n" +
                "  • Canvas > Sort Order: 32000. Higher numbers draw in front of your other UI.\n" +
                "  • Canvas Scaler > UI Scale Mode: Scale With Screen Size, Reference Resolution 1920 x 1080.\n" +
                "  • Remove the Graphic Raycaster component. A toast must never take the player's clicks.");

            Step(2, "Add the Panel",
                "Right-click the Canvas, UI, Image. Name it Panel. This is the box that slides in and out.\n" +
                "  • Give it a fixed Width and Height (for example 440 x 108) and any color or sprite.\n" +
                "  • Do not stretch it. The game sets its anchors and pivot from the 'Screen position' setting and moves it for the animation.\n" +
                "  • Add Component, Canvas Group. Untick Interactable and Blocks Raycasts. The fade uses this.");

            Step(3, "Add the contents",
                "Right-click the Panel, UI, and add these, named exactly like this (any layout, any size):\n" +
                "  • Icon: an Image, with Preserve Aspect on. Shows the achievement's picture. Optional.\n" +
                "  • Header: text for the 'ACHIEVEMENT UNLOCKED' line. Optional.\n" +
                "  • Title: text for the achievement's name. Required.\n" +
                "  • Description: text for what the player did. Optional.\n" +
                "Text can be the classic 'Text (Legacy)' or 'Text - TextMeshPro'. Untick Raycast Target on all of them.");

            Step(4, "Add the Achievement Toast View",
                "Select the Canvas (the root of the toast), Add Component, search 'Achievement Toast View'.\n" +
                "  • Classic Text: 'Achievement Toast View'.\n" +
                "  • TextMeshPro: 'Achievement Toast View (TextMeshPro)'. It only appears when TextMeshPro is in the project.\n" +
                "This script is the one thing the game talks to: it sets the texts and icon and does the sliding.");

            Step(5, "Connect the fields",
                "Drag each object from the Hierarchy into the matching field of Achievement Toast View:\n" +
                "  • Panel → the Panel object.\n" +
                "  • Canvas Group → the Panel (its Canvas Group component).\n" +
                "  • Icon → the Icon object.\n" +
                "  • Header, Title, Description → the text objects (for TextMeshPro, the three TMP fields instead).\n" +
                "  • Icon Background: leave empty. It is created for you when the game uses Layered icons.\n" +
                "Shortcut: once the prefab exists (step 6), the 'Connect fields automatically' button below fills every empty field by matching those object names.");

            Step(6, "Save it as a prefab inside Resources",
                "Drag the Canvas from the Hierarchy into " + AchievementOverlayFile.PrefabFolder + "/ in the Project window (the folder must be inside a folder named Resources: the game loads the toast by name). Then delete the copy from the scene.\n" +
                "Do not leave the toast in your scenes: the game creates its own copy.");

            Step(7, "Tell the overlay to use it",
                "Drag the prefab into 'Your toast prefab' below and press 'Use this prefab'. The dashboard stores its Resources path in overlay.json; nothing needs to be dragged into a scene.\n" +
                "Then press Play, go to 'Customize the toast' and press 'Show a test toast in the game'. Edit the prefab, save it, and press the button again to iterate.");

            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                EditorGUILayout.LabelField("What still comes from the dashboard", _styles.CardTitle);
                EditorGUILayout.LabelField(
                    "With your own prefab the 'Customize' page still controls: screen position, margins, size, animation, all timings, the sound and the header text. " +
                    "Colors, fonts, corner radius and shadow belong to your prefab, so the dashboard leaves them alone. Titles, descriptions and icons come from your achievements " +
                    "(including their localization and the Combined / Layered icon style); you never write that code.",
                    EditorStyles.wordWrappedLabel);
            }

            DrawPrefabChecker();
        }

        private void Step(int number, string title, string body)
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                EditorGUILayout.LabelField(number + ".  " + title, _styles.CardTitle);
                EditorGUILayout.LabelField(body, EditorStyles.wordWrappedLabel);
            }
        }

        private void CreateStarter()
        {
            try
            {
                string path = AchievementToastPrefabTools.CreateStarterPrefab(AchievementOverlayFile.DefaultPrefabPath);
                _toastPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                _toastChecksFor = null;
                Selection.activeObject = _toastPrefabAsset;
                EditorGUIUtility.PingObject(_toastPrefabAsset);
                _overlayNote = "Created " + path + ". Double-click it to restyle it, then press 'Use this prefab' below.";
            }
            catch (Exception e)
            {
                _overlayNote = "Could not create the starter prefab: " + DashboardData.Describe(e);
                Debug.LogException(e);
            }
        }

        private void DrawPrefabChecker()
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                EditorGUILayout.LabelField("Check and use your prefab", _styles.CardTitle);

                if (_toastPrefabAsset == null && _overlay.UsesCustomPrefab)
                    _toastPrefabAsset = Resources.Load<GameObject>(_overlay.toastPrefabResource);

                _toastPrefabAsset = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Your toast prefab", "The prefab asset (not a scene object)."), _toastPrefabAsset, typeof(GameObject), false);

                string assetPath = _toastPrefabAsset != null ? AssetDatabase.GetAssetPath(_toastPrefabAsset) : null;
                if (_toastPrefabAsset != null && string.IsNullOrEmpty(assetPath))
                {
                    EditorGUILayout.HelpBox("That is a scene object. Drag the toast into the Project window to make a prefab first (step 6), then pick the prefab here.", MessageType.Warning);
                    return;
                }

                if (_toastPrefabAsset == null)
                {
                    EditorGUILayout.LabelField("Pick your prefab to see what is connected and what is missing.", _styles.Mini);
                    DrawStopUsingButton();
                    return;
                }

                if (Event.current.type == EventType.Layout && (_toastChecksFor != assetPath || _toastChecksDirty))
                {
                    _toastChecks = AchievementToastPrefabTools.Check(_toastPrefabAsset);
                    _toastChecksFor = assetPath;
                    _toastChecksDirty = false;
                }

                foreach (var check in _toastChecks)
                {
                    var type = check.Level == ToastCheckLevel.Ok ? MessageType.None : check.Level == ToastCheckLevel.Warning ? MessageType.Warning : MessageType.Error;
                    string icon = check.Level == ToastCheckLevel.Ok ? "✓  " : check.Level == ToastCheckLevel.Warning ? "!  " : "✗  ";
                    if (check.Level == ToastCheckLevel.Ok) EditorGUILayout.LabelField(icon + check.Message, _styles.Mini);
                    else EditorGUILayout.HelpBox(check.Message, type);
                    if (check.Fix != ToastFix.None && check.Fix != ToastFix.AutoWire && GUILayout.Button(FixLabel(check.Fix), GUILayout.Width(220)))
                        RunToastFix(assetPath, check.Fix);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Connect fields automatically", GUILayout.Height(24))) RunToastFix(assetPath, ToastFix.AutoWire);
                    if (GUILayout.Button("Check again", GUILayout.Height(24), GUILayout.Width(100))) _toastChecksDirty = true;
                }

                bool blocked = _toastChecks.Any(c => c.Level == ToastCheckLevel.Error);
                string resource = AchievementResourcePaths.FromAssetPath(assetPath);
                using (new EditorGUI.DisabledScope(blocked || resource == null))
                {
                    var previous = GUI.backgroundColor;
                    GUI.backgroundColor = Palette.Accent;
                    if (GUILayout.Button(_overlay.toastPrefabResource == resource ? "✓  The game uses this prefab" : "Use this prefab", GUILayout.Height(28)) && _overlay.toastPrefabResource != resource)
                    {
                        _overlay.toastPrefabResource = resource;
                        OverlayChanged();
                    }
                    GUI.backgroundColor = previous;
                }
                if (blocked) EditorGUILayout.LabelField("Fix the red items first.", _styles.Mini);
                else if (resource == null) EditorGUILayout.LabelField("Move the prefab into a Resources folder first.", _styles.Mini);

                DrawStopUsingButton();
            }
        }

        private bool _toastChecksDirty;

        private void DrawStopUsingButton()
        {
            if (_overlay.UsesCustomPrefab && GUILayout.Button("Go back to the built-in toast", EditorStyles.miniButton, GUILayout.Width(200)))
            {
                _overlay.toastPrefabResource = "";
                OverlayChanged();
            }
        }

        private static string FixLabel(ToastFix fix)
        {
            switch (fix)
            {
                case ToastFix.RemoveRaycaster: return "Remove the Graphic Raycaster";
                case ToastFix.MakeNonBlocking: return "Make the Canvas Group non-blocking";
                case ToastFix.AddCanvasGroup: return "Add a Canvas Group to the Panel";
                case ToastFix.MoveToResources: return "Move it into Resources";
                default: return "Fix";
            }
        }

        private void RunToastFix(string prefabPath, ToastFix fix)
        {
            try
            {
                _overlayNote = AchievementToastPrefabTools.ApplyFix(prefabPath, fix);
                if (fix == ToastFix.MoveToResources) _toastPrefabAsset = null;
            }
            catch (Exception e)
            {
                _overlayNote = "Could not change the prefab: " + DashboardData.Describe(e);
                Debug.LogException(e);
            }
            _toastChecksDirty = true;
            _toastChecksFor = null;
        }
    }
}
