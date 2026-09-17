using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Generates a reusable 9-sliced procedural rounded-rectangle sprite with anti-aliasing.
    /// </summary>
    internal static class RoundedBoxSprite
    {
        private static Sprite _cached14;

        public static Sprite GetOrCreate(int radius = 14)
        {
            if (radius == 14 && _cached14 != null) return _cached14;

            int size = radius * 2 + 4;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "AchievementToastRoundedBox",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Find the nearest corner center
                    int cx = x < radius ? radius : (x >= size - radius ? size - 1 - radius : x);
                    int cy = y < radius ? radius : (y >= size - radius ? size - 1 - radius : y);

                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // 1px anti-aliased soft edge
                    float alpha = Mathf.Clamp01(radius + 0.5f - dist);
                    byte a = (byte)(alpha * 255f);

                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sprite.name = "AchievementToastRoundedBox_" + radius;

            if (radius == 14) _cached14 = sprite;
            return sprite;
        }
    }

    /// <summary>
    /// Visual for one toast. The default implementation builds a simple uGUI panel in code; subclass it
    /// (e.g. for TextMeshPro or a designed prefab) and assign the prefab to <see cref="UnityAchievementOverlay"/>.
    /// </summary>
    public class AchievementToastView : MonoBehaviour
    {
        [SerializeField] private RectTransform _panel;
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _accent;
        [SerializeField] private Image _icon;
        [SerializeField] private Text _header;
        [SerializeField] private Text _title;
        [SerializeField] private Text _description;

        private Vector2 _restingPosition;

        public virtual void SetContent(string header, string title, string description, Sprite icon)
        {
            if (_header != null) _header.text = header;
            if (_title != null) _title.text = title;
            if (_description != null) _description.text = description;
            if (_icon != null)
            {
                _icon.sprite = icon;
                _icon.enabled = icon != null;
            }
        }

        public string TitleText => _title != null ? _title.text : null;

        public string DescriptionText => _description != null ? _description.text : null;

        public bool HasIcon => _icon != null && _icon.enabled;

        public Sprite Icon => _icon != null ? _icon.sprite : null;

        public RectTransform Accent => _accent;

        public virtual void SetText(string title, string description)
        {
            if (_title != null) _title.text = title;
            if (_description != null) _description.text = description;
        }

        /// <summary>Swaps in a resolved icon after showing a fallback first (e.g. once a network download completes).</summary>
        public virtual void SetIcon(Sprite icon)
        {
            if (_icon == null) return;
            _icon.sprite = icon;
            _icon.enabled = icon != null;
        }

        /// <summary>
        /// 0 = hidden, tucked below the bottom edge of the screen; 1 = fully shown at its resting position
        /// (see <see cref="SetMargin"/>). Slides straight up on enter and straight back down on exit, sized
        /// off the panel's own <see cref="RectTransform.rect"/> height so this works for any prefab, not just
        /// the built-in default.
        /// </summary>
        public virtual void SetVisibility(float visibility)
        {
            if (_canvasGroup != null) _canvasGroup.alpha = visibility;
            if (_panel != null)
            {
                float hiddenDrop = _panel.rect.height + 24f; // fully below the screen edge, plus a small buffer
                _panel.anchoredPosition = _restingPosition + new Vector2(0f, -(1f - visibility) * hiddenDrop);
            }
        }

        public virtual void SetMargin(Vector2 margin)
        {
            _restingPosition = new Vector2(-margin.x, margin.y);
            if (_panel != null) _panel.anchoredPosition = _restingPosition;
        }

        /// <summary>Builds the default bottom-right toast. Used when no prefab is assigned.</summary>
        public static AchievementToastView CreateDefault(Transform parent, int sortingOrder, Font customFont = null, Color? accentColor = null, int cornerRadius = 14)
        {
            var root = new GameObject("AchievementToast", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster: the toast must never swallow gameplay input.

            var view = root.AddComponent<AchievementToastView>();
            var font = customFont != null ? customFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                // Broad system font fallback supporting Cyrillic, Turkish, Latin, and CJK
                font.fontNames = new string[]
                {
                    font.name,
                    "Segoe UI",              // Windows default (Cyrillic, Turkish, Latin)
                    "Arial",                 // Universal cross-platform
                    "Tahoma",                // Broad Unicode coverage
                    "Verdana",
                    "Microsoft YaHei",       // Windows Chinese
                    "SimSun",                // Windows Chinese fallback
                    "PingFang SC",           // macOS Chinese
                    "Heiti SC",              // macOS Chinese
                    "Helvetica Neue",        // macOS Latin
                    "Noto Sans CJK SC",      // Android / Linux Chinese
                    "Noto Sans",             // Android / Linux Latin/Cyrillic
                    "Roboto",                // Android default
                    "Droid Sans Fallback"    // Android fallback
                };
            }

            // Panel: 440 x 108, anchored to bottom-right
            view._panel = CreateRect("Panel", root.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(440, 108));
            var background = view._panel.gameObject.AddComponent<Image>();
            background.sprite = RoundedBoxSprite.GetOrCreate(cornerRadius);
            background.type = Image.Type.Sliced;
            // Background color matches target (#14121D, RGB: 20, 18, 29) with 100% opacity
            background.color = new Color32(20, 18, 29, 255);
            background.raycastTarget = false;
            view._canvasGroup = view._panel.gameObject.AddComponent<CanvasGroup>();
            view._canvasGroup.interactable = false;
            view._canvasGroup.blocksRaycasts = false;

            Color effectiveAccentColor = accentColor ?? Color.white;

            // Accent bar on right edge: collapsed (width 0, inactive) by default, kept in hierarchy
            var accent = CreateRect("Accent", view._panel, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f), Vector2.zero);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = effectiveAccentColor;
            accentImage.raycastTarget = false;
            accent.gameObject.SetActive(false);
            view._accent = accent;

            // Icon: 72x72, left aligned at x=20, vertically centered
            var icon = CreateRect("Icon", view._panel, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, 72));
            icon.anchoredPosition = new Vector2(20, 0);
            view._icon = icon.gameObject.AddComponent<Image>();
            view._icon.preserveAspect = true;
            view._icon.raycastTarget = false;

            // Text components with BestFit enabled and strict bounds to guarantee text never overflows
            // Available text width: 440 - 108 (left) - 16 (right margin) = 316
            view._header = CreateText("Header", view._panel, font, 12, 9, FontStyle.Bold, effectiveAccentColor, 108, 12, 316, 16);
            view._title = CreateText("Title", view._panel, font, 18, 12, FontStyle.Bold, Color.white, 108, 30, 316, 27);
            view._description = CreateText("Description", view._panel, font, 14, 10, FontStyle.Normal, new Color(0.82f, 0.82f, 0.88f), 108, 58, 316, 42);

            view.SetMargin(Vector2.zero);
            view.SetVisibility(0f);
            return view;
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            return rect;
        }

        private static Text CreateText(string name, Transform parent, Font font, int maxSize, int minSize, FontStyle style, Color color, float left, float top, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(left, -top);
            rect.sizeDelta = new Vector2(width, height);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = maxSize;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = minSize;
            text.resizeTextMaxSize = maxSize;
            text.fontStyle = style;
            text.color = color;
            text.raycastTarget = false;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.lineSpacing = 1.05f;
            return text;
        }
    }

    /// <summary>
    /// Shows queued achievement notifications one at a time in the bottom-right corner:
    /// fade/slide in, hold, fade/slide out. A single view instance is reused for every toast, the sound
    /// plays once per toast, and animation uses unscaled time so it works while the game is paused.
    /// </summary>
    public sealed class UnityAchievementOverlay : MonoBehaviour
    {
        private enum Phase { Idle, WaitingForText, Enter, Hold, Exit }

        private static readonly Dictionary<string, string> FallbackHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "en", "ACHIEVEMENT UNLOCKED" },
            { "tr", "BAŞARIM AÇILDI" },
            { "es", "LOGRO DESBLOQUEADO" },
            { "fr", "SUCCÈS DÉVERROUILLÉ" },
            { "ru", "ДОСТИЖЕНИЕ РАЗБЛОКИРОВАНО" }
        };

        [Header("Presentation")]
        [SerializeField] private AchievementToastView _toastPrefab;
        [SerializeField] private string _headerText = "ACHIEVEMENT UNLOCKED";
        [SerializeField] private string _headerLocalizationTable = "ST_Achievements";
        [SerializeField] private string _headerLocalizationKey = "achievement_unlocked";
        [SerializeField] private Vector2 _margin = Vector2.zero;
        [SerializeField] private int _sortingOrder = 32000;
        [SerializeField] private Font _customFont;
        [SerializeField] private Color _accentColor = Color.white;
        [Range(0, 30)]
        [SerializeField] private int _cornerRadius = 14;

        [Header("Timing (seconds)")]
        [SerializeField] private float _enterDuration = 0.25f;
        [SerializeField] private float _holdDuration = 4.5f;
        [SerializeField] private float _exitDuration = 0.5f;
        [Tooltip("How long to wait for localized text before showing fallback text.")]
        [SerializeField] private float _localizationGrace = 0.2f;

        [Header("Audio")]
        [SerializeField] private AudioClip _unlockSound;
        [SerializeField, Range(0f, 1f)] private float _volume = 0.8f;

        private AchievementNotificationService _service;
        private IAchievementIconProvider _icons;
        private AchievementToastView _view;
        private AudioSource _audio;
        private AchievementNotification _current;
        private Phase _phase;
        private float _phaseTime;
        private int _textVersion;
        private bool _iconFinal = true;
        private Task<Sprite> _pendingIconTask;

        public bool IsShowing => _phase != Phase.Idle;

        /// <summary>Number of toasts shown since startup (diagnostics/tests).</summary>
        public int ShownCount { get; private set; }

        /// <summary>Number of times the unlock sound was started (diagnostics/tests).</summary>
        public int SoundPlayCount { get; private set; }

        /// <summary>The reused toast view, or null before the first toast.</summary>
        public AchievementToastView View => _view;

        public Font CustomFont
        {
            get => _customFont;
            set => _customFont = value;
        }

        public Color AccentColor
        {
            get => _accentColor;
            set => _accentColor = value;
        }

        public int CornerRadius
        {
            get => _cornerRadius;
            set => _cornerRadius = Mathf.Max(0, value);
        }

        public Vector2 Margin
        {
            get => _margin;
            set
            {
                _margin = value;
                if (_view != null) _view.SetMargin(_margin);
            }
        }

        public void SetTimings(float enter, float hold, float exit, float localizationGrace)
        {
            _enterDuration = Mathf.Max(0f, enter);
            _holdDuration = Mathf.Max(0f, hold);
            _exitDuration = Mathf.Max(0f, exit);
            _localizationGrace = Mathf.Max(0f, localizationGrace);
        }

        public float HoldDuration
        {
            get => _holdDuration;
            set => _holdDuration = Mathf.Max(0f, value);
        }

        public AudioClip UnlockSound
        {
            get => _unlockSound;
            set => _unlockSound = value;
        }

        public void Bind(AchievementNotificationService service, IAchievementIconProvider icons)
        {
            _service = service;
            _icons = icons ?? new ResourcesAchievementIconProvider();
        }

        private void Update()
        {
            if (_service == null) return;
            float dt = Time.unscaledDeltaTime;

            switch (_phase)
            {
                case Phase.Idle:
                    TryStartNext();
                    break;
                case Phase.WaitingForText:
                    _phaseTime += dt;
                    if (_current.IsTextFinal || _phaseTime >= _localizationGrace) Begin();
                    break;
                case Phase.Enter:
                    _phaseTime += dt;
                    RefreshTextIfChanged();
                    RefreshIconIfReady();
                    _view.SetVisibility(Ease(_enterDuration <= 0f ? 1f : _phaseTime / _enterDuration));
                    if (_phaseTime >= _enterDuration) SetPhase(Phase.Hold);
                    break;
                case Phase.Hold:
                    _phaseTime += dt;
                    RefreshTextIfChanged();
                    RefreshIconIfReady();
                    if (_phaseTime >= _holdDuration) SetPhase(Phase.Exit);
                    break;
                case Phase.Exit:
                    _phaseTime += dt;
                    _view.SetVisibility(1f - Ease(_exitDuration <= 0f ? 1f : _phaseTime / _exitDuration));
                    if (_phaseTime >= _exitDuration) Finish();
                    break;
            }
        }

        private void TryStartNext()
        {
            if (!_service.Queue.TryDequeue(out var next)) return;

            // The launcher setting may have changed while the toast waited in the queue.
            _service.Settings.Refresh();
            if (!_service.Settings.NotificationsEnabled) return;

            _current = next;
            if (next.IsTextFinal) Begin();
            else SetPhase(Phase.WaitingForText);
        }

        private void Begin()
        {
            EnsureView();
            _textVersion = _current.TextVersion; 
            Sprite icon = null;
            _iconFinal = true;
            _pendingIconTask = null;
            try
            {
                icon = _icons.GetIcon(_current.Definition, out _iconFinal);
                if (!_iconFinal) _pendingIconTask = _icons.GetIconAsync(_current.Definition, CancellationToken.None);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                _iconFinal = true;
            }

            string localizedHeader = ResolveLocalizedHeader();
            _view.SetContent(localizedHeader, _current.Title, _current.Description, icon);
            _view.SetVisibility(0f);
            _view.gameObject.SetActive(true);
            ShownCount++;

            if (_unlockSound != null)
            {
                if (_audio == null)
                {
                    _audio = gameObject.AddComponent<AudioSource>();
                    _audio.playOnAwake = false;
                    _audio.ignoreListenerPause = true;
                }
                _audio.PlayOneShot(_unlockSound, _volume);
                SoundPlayCount++;
            }
            SetPhase(Phase.Enter);
        }

        private void Finish()
        {
            _view.SetVisibility(0f);
            _view.gameObject.SetActive(false); // kept for reuse
            _current = null;
            SetPhase(Phase.Idle);
        }

        private void RefreshTextIfChanged()
        {
            int version = _current.TextVersion;
            if (version == _textVersion) return;
            _textVersion = version;
            _view.SetText(_current.Title, _current.Description);
        }

        private void RefreshIconIfReady()
        {
            if (_iconFinal || _pendingIconTask == null || !_pendingIconTask.IsCompleted) return;
            _iconFinal = true;
            try
            {
                _view.SetIcon(_pendingIconTask.Result);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            _pendingIconTask = null;
        }

        private void EnsureView()
        {
            if (_view != null) return;
            if (_toastPrefab != null)
            {
                _view = Instantiate(_toastPrefab, transform);
            }
            else
            {
                _view = AchievementToastView.CreateDefault(transform, _sortingOrder, _customFont, _accentColor, _cornerRadius);
            }
            _view.SetMargin(_margin);
            _view.gameObject.SetActive(false);
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _phaseTime = 0f;
        }

        private string ResolveLocalizedHeader()
        {
            // 1. Attempt lookup via Unity Localization StringDatabase
            if (!string.IsNullOrEmpty(_headerLocalizationTable) && !string.IsNullOrEmpty(_headerLocalizationKey))
            {
                string text = TryLookupLocalizedString(_headerLocalizationTable, _headerLocalizationKey);
                if (!string.IsNullOrEmpty(text)) return text;
            }

            // 2. Fall back to supported system language if active language has no translation
            string sysCode = GetSupportedLanguageCode(Application.systemLanguage);
            if (FallbackHeaders.TryGetValue(sysCode, out var sysHeader))
            {
                return sysHeader;
            }

            // 3. Fall back to inspector default
            return !string.IsNullOrEmpty(_headerText) ? _headerText : "ACHIEVEMENT UNLOCKED";
        }

        private static string TryLookupLocalizedString(string table, string key)
        {
            try
            {
                var locType = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
                if (locType == null) return null;

                var dbProp = locType.GetProperty("StringDatabase", BindingFlags.Public | BindingFlags.Static);
                var db = dbProp?.GetValue(null);
                if (db == null) return null;

                var method = db.GetType().GetMethod("GetLocalizedString", new Type[] { typeof(string), typeof(string) });
                if (method != null)
                {
                    var result = method.Invoke(db, new object[] { table, key }) as string;
                    if (!string.IsNullOrEmpty(result)) return result;
                }
            }
            catch { }
            return null;
        }

        private static string GetSupportedLanguageCode(SystemLanguage lang)
        {
            switch (lang)
            {
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.English: return "en";
                default: return "en"; // If system language is unsupported, fallback to English
            }
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t); // ease-out cubic
        }
    }
}
