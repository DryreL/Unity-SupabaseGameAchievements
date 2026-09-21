using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Generates a procedural rounded-rectangle sprite with anti-aliasing and a diagonal gradient.
    /// </summary>
    internal static class RoundedBoxSprite
    {
        private const int MaxCachedSprites = 8;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        private static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj);
        }

        public static Sprite GetOrCreateGradient(int width = 440, int height = 108, int radius = 14)
            => GetOrCreateGradient(width, height, radius, new Color32(20, 18, 29, 255), new Color32(10, 9, 15, 255));

        /// <summary>A rounded box fading diagonally from <paramref name="startColor"/> (top-left) to <paramref name="endColor"/> (bottom-right).</summary>
        public static Sprite GetOrCreateGradient(int width, int height, int radius, Color32 startColor, Color32 endColor)
        {
            string key = width + "x" + height + "r" + radius + "/" + startColor.r + "," + startColor.g + "," + startColor.b + "," + startColor.a +
                         "/" + endColor.r + "," + endColor.g + "," + endColor.b + "," + endColor.a;
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            // Live-editing colors in Play Mode would otherwise leave one texture behind per tweak.
            if (Cache.Count >= MaxCachedSprites)
            {
                foreach (var old in Cache.Values)
                {
                    if (old == null) continue;
                    DestroyObject(old.texture);
                    DestroyObject(old);
                }
                Cache.Clear();
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "AchievementToastGradientBg",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[width * height];

            float boxHalfW = width * 0.5f;
            float boxHalfH = height * 0.5f;
            float innerW = boxHalfW - radius;
            float innerH = boxHalfH - radius;

            for (int y = 0; y < height; y++)
            {
                // In Texture2D: y=0 is bottom, y=height-1 is top
                float normY = 1f - ((float)y / (height - 1)); // 0 at top, 1 at bottom
                float py = Mathf.Abs((y + 0.5f) - boxHalfH) - innerH;
                float dy = Mathf.Max(0f, py);

                for (int x = 0; x < width; x++)
                {
                    float normX = (float)x / (width - 1); // 0 at left, 1 at right

                    // Diagonal gradient factor from top-left (0) to bottom-right (1)
                    float diagT = Mathf.Clamp01((normX + normY) * 0.5f);

                    byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(startColor.r, endColor.r, diagT));
                    byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(startColor.g, endColor.g, diagT));
                    byte b = (byte)Mathf.RoundToInt(Mathf.Lerp(startColor.b, endColor.b, diagT));
                    float fillAlpha = Mathf.Lerp(startColor.a, endColor.a, diagT) / 255f;

                    // Signed distance field for rounded corner mask
                    float px = Mathf.Abs((x + 0.5f) - boxHalfW) - innerW;
                    float dx = Mathf.Max(0f, px);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float signedDist = dist - radius;

                    float alpha = Mathf.Clamp01(0.5f - signedDist);
                    byte a = (byte)Mathf.RoundToInt(alpha * fillAlpha * 255f);

                    pixels[y * width + x] = new Color32(r, g, b, a);
                }
            }

            texture.hideFlags = HideFlags.DontSave; // an Editor preview must not leave these in a scene
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.name = "AchievementToastGradientBg";
            Cache[key] = sprite;
            return sprite;
        }
    }

    /// <summary>
    /// Visual for one toast.
    /// </summary>
    public class AchievementToastView : MonoBehaviour
    {
        [SerializeField] private RectTransform _panel;
        [SerializeField] private CanvasGroup _canvasGroup;
        [SerializeField] private RectTransform _accent;
        [SerializeField] private Image _icon;
        [Tooltip("Optional. Shared background drawn behind the icon in the Layered icon style. Created automatically behind the Icon if left empty.")]
        [SerializeField] private Image _iconBackground;
        [SerializeField] private Text _header;
        [SerializeField] private Text _title;
        [SerializeField] private Text _description;

        private Vector2 _restingPosition;
        private Vector2 _margin;
        private AchievementToastCorner _corner = AchievementToastCorner.BottomRight;
        private AchievementToastAnimation _animation = AchievementToastAnimation.Slide;
        private float _scale = 1f;
        private bool _iconRectCaptured;
        private AchievementIconLayout.RectSpec _combinedIconRect;

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

        public virtual string TitleText => _title != null ? _title.text : null;

        public virtual string DescriptionText => _description != null ? _description.text : null;

        public bool HasIcon => _icon != null && _icon.enabled;

        public RectTransform IconRect => _icon != null ? _icon.rectTransform : null;

        /// <summary>The shared background Image of the Layered icon style, or null while the Combined style is in use.</summary>
        public Image IconBackground => _iconBackground != null && _iconBackground.gameObject.activeSelf ? _iconBackground : null;

        /// <summary>
        /// Switches between the Combined style (the icon image carries its own background) and the Layered style
        /// (<paramref name="background"/> behind, the achievement icon shrunk by <paramref name="inset"/> on top).
        /// Layered without a background sprite is Combined. Safe to call repeatedly.
        /// </summary>
        public virtual void ApplyIconStyle(AchievementIconStyle style, Sprite background, float inset)
        {
            if (_icon == null) return;
            var iconRect = _icon.rectTransform;
            if (!_iconRectCaptured)
            {
                _combinedIconRect = AchievementIconLayout.Capture(iconRect);
                _iconRectCaptured = true;
            }

            if (style == AchievementIconStyle.Layered && background != null)
            {
                if (_iconBackground == null) _iconBackground = CreateIconBackground(iconRect, _combinedIconRect);
                _iconBackground.sprite = background;
                _iconBackground.enabled = true;
                _iconBackground.gameObject.SetActive(true);
                AchievementIconLayout.Apply(iconRect, AchievementIconLayout.Inset(_combinedIconRect, inset));
            }
            else
            {
                AchievementIconLayout.Apply(iconRect, _combinedIconRect);
                if (_iconBackground != null) _iconBackground.gameObject.SetActive(false);
            }
        }

        private static Image CreateIconBackground(RectTransform iconRect, AchievementIconLayout.RectSpec spec)
        {
            var go = new GameObject("IconBackground", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(iconRect.parent, false);
            AchievementIconLayout.Apply(rect, spec);
            rect.SetSiblingIndex(iconRect.GetSiblingIndex()); // directly behind the icon

            var image = go.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        public Sprite Icon => _icon != null ? _icon.sprite : null;

        public RectTransform Accent => _accent;

        public virtual void SetText(string title, string description)
        {
            if (_title != null) _title.text = title;
            if (_description != null) _description.text = description;
        }

        public virtual void SetIcon(Sprite icon)
        {
            if (_icon == null) return;
            _icon.sprite = icon;
            _icon.enabled = icon != null;
        }

        public AchievementToastCorner Corner => _corner;

        public AchievementToastAnimation Animation => _animation;

        /// <summary>The panel the overlay moves and fades (the Panel field). Null when the view has none.</summary>
        public RectTransform Panel => _panel;

        public virtual void SetVisibility(float visibility)
        {
            visibility = Mathf.Clamp01(visibility);
            if (_canvasGroup != null) _canvasGroup.alpha = visibility;
            if (_panel == null) return;

            Vector2 position = _restingPosition;
            float scale = _scale;
            switch (_animation)
            {
                case AchievementToastAnimation.Slide:
                    // Leaves through the edge the toast sits against: down for the bottom corners, up for the top ones.
                    float hiddenDrop = _panel.rect.height * _scale + 24f;
                    position += new Vector2(0f, SlideSign(_corner) * (1f - visibility) * hiddenDrop);
                    break;
                case AchievementToastAnimation.Pop:
                    scale = _scale * Mathf.Lerp(0.8f, 1f, visibility);
                    break;
            }

            _panel.anchoredPosition = position;
            _panel.localScale = new Vector3(scale, scale, 1f);
        }

        public virtual void SetMargin(Vector2 margin)
        {
            _margin = margin;
            ApplyResting();
        }

        /// <summary>
        /// Anchors the panel to a screen corner (or the middle of the top/bottom edge) and picks the slide direction.
        /// The panel's own anchors and pivot are replaced: its size and children stay as designed.
        /// </summary>
        public virtual void SetCorner(AchievementToastCorner corner)
        {
            _corner = corner;
            if (_panel != null)
            {
                Vector2 anchor = CornerAnchor(corner);
                _panel.anchorMin = anchor;
                _panel.anchorMax = anchor;
                _panel.pivot = anchor;
            }
            ApplyResting();
        }

        public virtual void SetAnimation(AchievementToastAnimation animation)
        {
            _animation = animation;
        }

        /// <summary>Uniform size multiplier for the whole panel.</summary>
        public virtual void SetScale(float scale)
        {
            _scale = Mathf.Max(0.01f, scale);
            if (_panel != null) _panel.localScale = new Vector3(_scale, _scale, 1f);
        }

        private void ApplyResting()
        {
            // Margins always push the toast inwards from its edge, whichever corner it sits in.
            float x = CornerAnchor(_corner).x;
            float xSign = x > 0.75f ? -1f : x < 0.25f ? 1f : 0f;
            _restingPosition = new Vector2(xSign * _margin.x, -SlideSign(_corner) * _margin.y);
            if (_panel != null) _panel.anchoredPosition = _restingPosition;
        }

        /// <summary>-1 for the bottom edge (slides down to hide), +1 for the top edge (slides up).</summary>
        internal static float SlideSign(AchievementToastCorner corner) =>
            corner == AchievementToastCorner.TopLeft || corner == AchievementToastCorner.TopRight || corner == AchievementToastCorner.TopCenter ? 1f : -1f;

        internal static Vector2 CornerAnchor(AchievementToastCorner corner)
        {
            switch (corner)
            {
                case AchievementToastCorner.BottomLeft: return new Vector2(0f, 0f);
                case AchievementToastCorner.TopRight: return new Vector2(1f, 1f);
                case AchievementToastCorner.TopLeft: return new Vector2(0f, 1f);
                case AchievementToastCorner.BottomCenter: return new Vector2(0.5f, 0f);
                case AchievementToastCorner.TopCenter: return new Vector2(0.5f, 1f);
                default: return new Vector2(1f, 0f);
            }
        }

        public static AchievementToastView CreateDefault(Transform parent, int sortingOrder, Font customFont = null, Color? accentColor = null, int cornerRadius = 14, int headerFontSize = 16, int titleFontSize = 24, int descriptionFontSize = 18)
        {
            var settings = AchievementOverlaySettings.CreateDefault();
            settings.sortingOrder = sortingOrder;
            settings.cornerRadius = cornerRadius;
            settings.headerFontSize = headerFontSize;
            settings.titleFontSize = titleFontSize;
            settings.descriptionFontSize = descriptionFontSize;
            if (accentColor.HasValue) settings.headerColor = AchievementOverlaySettings.ToHex(accentColor.Value);
            return CreateDefault(parent, settings, customFont);
        }

        /// <summary>Builds the built-in toast in code, styled by <paramref name="settings"/> (colors, fonts, radius, shadow, placement, motion).</summary>
        public static AchievementToastView CreateDefault(Transform parent, AchievementOverlaySettings settings, Font customFont = null)
        {
            settings = (settings ?? AchievementOverlaySettings.CreateDefault()).Clone().Sanitize();

            var root = new GameObject("AchievementToast", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = settings.sortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            var view = root.AddComponent<AchievementToastView>();
            var font = customFont != null ? customFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                font.fontNames = new string[]
                {
                    font.name,
                    "Segoe UI",
                    "Arial",
                    "Tahoma",
                    "Verdana",
                    "Microsoft YaHei",
                    "SimSun",
                    "PingFang SC",
                    "Heiti SC",
                    "Helvetica Neue",
                    "Noto Sans CJK SC",
                    "Noto Sans",
                    "Roboto",
                    "Droid Sans Fallback"
                };
            }

            view._panel = CreateRect("Panel", root.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(440, 108));
            var background = view._panel.gameObject.AddComponent<Image>();
            // Procedural gradient from the first background color (top-left) to the second (bottom-right).
            background.sprite = RoundedBoxSprite.GetOrCreateGradient(440, 108, settings.cornerRadius, settings.BackgroundColor, settings.BackgroundColor2);
            background.type = Image.Type.Simple;
            background.color = Color.white;
            background.raycastTarget = false;
            if (settings.shadowEnabled)
            {
                var shadow = view._panel.gameObject.AddComponent<Shadow>();
                shadow.effectColor = settings.ShadowColor;
                shadow.effectDistance = new Vector2(0f, -settings.shadowDistance);
                shadow.useGraphicAlpha = true;
            }
            view._canvasGroup = view._panel.gameObject.AddComponent<CanvasGroup>();
            view._canvasGroup.interactable = false;
            view._canvasGroup.blocksRaycasts = false;

            Color headerColor = settings.HeaderColor;

            var accent = CreateRect("Accent", view._panel, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f), Vector2.zero);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = headerColor;
            accentImage.raycastTarget = false;
            accent.gameObject.SetActive(false);
            view._accent = accent;

            var icon = CreateRect("Icon", view._panel, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, 72));
            icon.anchoredPosition = new Vector2(20, 0);
            view._icon = icon.gameObject.AddComponent<Image>();
            view._icon.preserveAspect = true;
            view._icon.raycastTarget = false;

            int safeHeaderFontSize = Mathf.Max(1, settings.headerFontSize);
            int safeTitleFontSize = Mathf.Max(1, settings.titleFontSize);
            int safeDescriptionFontSize = Mathf.Max(1, settings.descriptionFontSize);
            view._header = CreateText("Header", view._panel, font, safeHeaderFontSize, Mathf.Max(1, safeHeaderFontSize - 3), FontStyle.Bold, headerColor, 108, 10, 316, 20);
            view._title = CreateText("Title", view._panel, font, safeTitleFontSize, Mathf.Max(1, safeTitleFontSize - 6), FontStyle.Bold, settings.TitleColor, 108, 30, 316, 32);
            view._description = CreateText("Description", view._panel, font, safeDescriptionFontSize, Mathf.Max(1, safeDescriptionFontSize - 4), FontStyle.Normal, settings.DescriptionColor, 108, 64, 316, 36);

            view.SetCorner(settings.corner);
            view.SetAnimation(settings.animation);
            view.SetScale(settings.scale);
            view.SetMargin(new Vector2(settings.marginX, settings.marginY));
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
    /// Shows queued achievement notifications in the bottom-right corner.
    /// </summary>
    public sealed class UnityAchievementOverlay : MonoBehaviour
    {
        private enum Phase { Idle, WaitingForText, Enter, Hold, Exit }

        private static readonly Dictionary<string, string> GameLanguageHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        [SerializeField] private int _headerFontSize = 16;
        [SerializeField] private int _titleFontSize = 24;
        [SerializeField] private int _descriptionFontSize = 18;
        [Range(0, 30)]
        [SerializeField] private int _cornerRadius = 14;

        [Header("Timing (seconds)")]
        [SerializeField] private float _enterDuration = 0.25f;
        [SerializeField] private float _holdDuration = 4.5f;
        [SerializeField] private float _exitDuration = 0.5f;
        [SerializeField] private float _localizationGrace = 0.2f;
        [Tooltip("Pause between one toast leaving and the next one appearing.")]
        [SerializeField] private float _gapDuration;

        [Header("Audio")]
        [SerializeField] private AudioClip _unlockSound;
        [SerializeField, Range(0f, 1f)] private float _volume = 0.8f;

        private AchievementNotificationService _service;
        private IAchievementIconProvider _icons;
        private IAchievementLocalizationProvider _localization;
        private AchievementToastView _view;
        private AchievementOverlaySettings _applied;
        private bool _headerTextIsCustom;
        private float _cooldown;
        private AchievementIconStyle _iconStyle = AchievementIconStyle.Combined;
        private Sprite _iconBackground;
        private float _iconInset = AchievementCatalog.DefaultIconInset;
        private AudioSource _audio;
        private AchievementNotification _current;
        private Phase _phase;
        private float _phaseTime;
        private int _textVersion;
        private bool _iconFinal = true;
        private Task<Sprite> _pendingIconTask;

        public bool IsShowing => _phase != Phase.Idle;

        public int ShownCount { get; private set; }

        public int SoundPlayCount { get; private set; }

        public AchievementToastView View => _view;

        public AchievementIconStyle IconStyle => _iconStyle;

        /// <summary>
        /// Chooses how toast icons are composed. <see cref="AchievementIconStyle.Layered"/> needs a
        /// <paramref name="background"/> (the same image for every achievement); without one it stays Combined.
        /// </summary>
        public void ConfigureIconStyle(AchievementIconStyle style, Sprite background, float inset)
        {
            _iconStyle = style == AchievementIconStyle.Layered && background != null ? AchievementIconStyle.Layered : AchievementIconStyle.Combined;
            _iconBackground = background;
            _iconInset = inset;
            if (_view != null) _view.ApplyIconStyle(_iconStyle, _iconBackground, _iconInset);
        }

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

        public int HeaderFontSize
        {
            get => _headerFontSize;
            set => _headerFontSize = Mathf.Max(1, value);
        }

        public int TitleFontSize
        {
            get => _titleFontSize;
            set => _titleFontSize = Mathf.Max(1, value);
        }

        public int DescriptionFontSize
        {
            get => _descriptionFontSize;
            set => _descriptionFontSize = Mathf.Max(1, value);
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

        /// <summary>The settings last given to <see cref="ApplySettings"/>, or null while the Inspector values are in use.</summary>
        public AchievementOverlaySettings AppliedSettings => _applied;

        /// <summary>
        /// Restyles the toast from data (the dashboard's overlay.json): colors, fonts, placement, motion, timing, sound
        /// and an optional custom prefab. The toast currently on screen is dropped and rebuilt on the next unlock, so
        /// it is safe to call again whenever a setting changes, also while the game runs.
        /// </summary>
        public void ApplySettings(AchievementOverlaySettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var s = settings.Clone().Sanitize();
            _applied = s;

            _headerText = s.headerText;
            _headerTextIsCustom = !string.IsNullOrEmpty(s.headerText);
            _headerLocalizationTable = s.headerLocalizationTable;
            _headerLocalizationKey = s.headerLocalizationKey;
            _margin = new Vector2(s.marginX, s.marginY);
            _sortingOrder = s.sortingOrder;
            _accentColor = s.HeaderColor;
            _headerFontSize = s.headerFontSize;
            _titleFontSize = s.titleFontSize;
            _descriptionFontSize = s.descriptionFontSize;
            _cornerRadius = s.cornerRadius;
            SetTimings(s.enterDuration, s.holdDuration, s.exitDuration, s.localizationGrace);
            _gapDuration = s.gapDuration;
            _volume = s.volume;

            _customFont = LoadResource<Font>(s.fontResource, "font");
            _unlockSound = LoadResource<AudioClip>(s.soundResource, "sound");
            _toastPrefab = null;
            if (s.UsesCustomPrefab)
            {
                _toastPrefab = LoadResource<AchievementToastView>(s.toastPrefabResource, "toast prefab");
                if (_toastPrefab == null && Resources.Load<GameObject>(s.toastPrefabResource) != null)
                    Debug.LogWarning("[Achievements] The toast prefab '" + s.toastPrefabResource + "' has no AchievementToastView on its root object; using the built-in toast.", this);
            }

            DropView();
        }

        private T LoadResource<T>(string path, string what) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
            var asset = Resources.Load<T>(path);
            if (asset == null)
                Debug.LogWarning("[Achievements] The overlay " + what + " 'Resources/" + path + "' was not found; using the default. Check the path in the dashboard's Overlay tab.", this);
            return asset;
        }

        private void DropView()
        {
            if (_view != null)
            {
                if (Application.isPlaying) Destroy(_view.gameObject); else DestroyImmediate(_view.gameObject);
                _view = null;
            }
            _current = null;
            _pendingIconTask = null;
            SetPhase(Phase.Idle);
        }

        public void Bind(AchievementNotificationService service, IAchievementIconProvider icons)
        {
            Bind(service, icons, null);
        }

        public void Bind(AchievementNotificationService service, IAchievementIconProvider icons, IAchievementLocalizationProvider localization)
        {
            _service = service;
            _icons = icons ?? new ResourcesAchievementIconProvider();
            _localization = localization;
        }

        private void Update()
        {
            if (_service == null) return;
            float dt = Time.unscaledDeltaTime;

            switch (_phase)
            {
                case Phase.Idle:
                    if (_cooldown > 0f)
                    {
                        _cooldown -= dt;
                        break;
                    }
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
            string localizedTitle = ResolveTitle(_current);
            string localizedDesc = ResolveDescription(_current);

            _view.SetContent(localizedHeader, localizedTitle, localizedDesc, icon);
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
            _view.gameObject.SetActive(false);
            _current = null;
            _cooldown = _gapDuration;
            SetPhase(Phase.Idle);
        }

        private void RefreshTextIfChanged()
        {
            int version = _current.TextVersion;
            if (version == _textVersion) return;
            _textVersion = version;
            _view.SetText(ResolveTitle(_current), ResolveDescription(_current));
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
            var look = _applied ?? SettingsFromFields();
            if (_toastPrefab != null)
            {
                _view = Instantiate(_toastPrefab, transform);
            }
            else
            {
                _view = AchievementToastView.CreateDefault(transform, look, _customFont);
            }
            _view.SetCorner(look.corner);
            _view.SetAnimation(look.animation);
            _view.SetScale(look.scale);
            _view.SetMargin(_margin);
            _view.ApplyIconStyle(_iconStyle, _iconBackground, _iconInset);
            _view.gameObject.SetActive(false);
        }

        // What the Inspector fields describe, for when no settings file was applied.
        private AchievementOverlaySettings SettingsFromFields()
        {
            var look = AchievementOverlaySettings.CreateDefault();
            look.sortingOrder = _sortingOrder;
            look.headerColor = AchievementOverlaySettings.ToHex(_accentColor);
            look.headerFontSize = _headerFontSize;
            look.titleFontSize = _titleFontSize;
            look.descriptionFontSize = _descriptionFontSize;
            look.cornerRadius = _cornerRadius;
            return look.Sanitize();
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _phaseTime = 0f;
        }

        private string ResolveTitle(AchievementNotification notification)
        {
            if (notification?.Definition != null && notification.Definition.HasLocalization)
            {
                string loc = TryLookupLocalizedString(notification.Definition.LocalizationTable, notification.Definition.TitleKey);
                if (IsValidTranslation(loc)) return loc;
            }
            return notification != null ? notification.Title : string.Empty;
        }

        private string ResolveDescription(AchievementNotification notification)
        {
            if (notification?.Definition != null && notification.Definition.HasLocalization)
            {
                string loc = TryLookupLocalizedString(notification.Definition.LocalizationTable, notification.Definition.DescriptionKey);
                if (IsValidTranslation(loc)) return loc;
            }
            return notification != null ? notification.Description : string.Empty;
        }

        private string ResolveLocalizedHeader()
        {
            // 1. Try resolving header directly from StringDatabase in game language
            if (!string.IsNullOrEmpty(_headerLocalizationTable) && !string.IsNullOrEmpty(_headerLocalizationKey))
            {
                string text = TryLookupLocalizedString(_headerLocalizationTable, _headerLocalizationKey);
                if (IsValidTranslation(text)) return text;
            }

            // Text typed into the dashboard beats the built-in per-language defaults below.
            if (_headerTextIsCustom && !string.IsNullOrEmpty(_headerText)) return _headerText;

            // 2. Strict fallback based on OYUN DİLİ (Game Language)
            string gameLanguage = GetGameLanguageCode();
            if (GameLanguageHeaders.TryGetValue(gameLanguage, out var header))
            {
                return header;
            }

            // 3. Fallback to inspector default
            return !string.IsNullOrEmpty(_headerText) ? _headerText : "ACHIEVEMENT UNLOCKED";
        }

        public static string GetGameLanguageCode()
        {
            try
            {
                var locType = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
                if (locType != null)
                {
                    var prop = locType.GetProperty("SelectedLocale", BindingFlags.Public | BindingFlags.Static);
                    var locale = prop?.GetValue(null);
                    if (locale != null)
                    {
                        var idProp = locale.GetType().GetProperty("Identifier");
                        var id = idProp?.GetValue(locale);
                        if (id != null)
                        {
                            var codeProp = id.GetType().GetProperty("Code");
                            string code = codeProp?.GetValue(id) as string;
                            if (!string.IsNullOrEmpty(code))
                            {
                                int dash = code.IndexOf('-');
                                if (dash > 0) code = code.Substring(0, dash);
                                return code.ToLowerInvariant();
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (PlayerPrefs.HasKey("OptionPicker_Language"))
                {
                    int idx = PlayerPrefs.GetInt("OptionPicker_Language", 0);
                    var locType = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
                    if (locType != null)
                    {
                        var availProp = locType.GetProperty("AvailableLocales", BindingFlags.Public | BindingFlags.Static);
                        var avail = availProp?.GetValue(null);
                        if (avail != null)
                        {
                            var localesProp = avail.GetType().GetProperty("Locales");
                            var locales = localesProp?.GetValue(avail) as IList;
                            if (locales != null && idx >= 0 && idx < locales.Count)
                            {
                                var loc = locales[idx];
                                var idProp = loc?.GetType().GetProperty("Identifier");
                                var id = idProp?.GetValue(loc);
                                var codeProp = id?.GetType().GetProperty("Code");
                                string code = codeProp?.GetValue(id) as string;
                                if (!string.IsNullOrEmpty(code))
                                {
                                    int dash = code.IndexOf('-');
                                    if (dash > 0) code = code.Substring(0, dash);
                                    return code.ToLowerInvariant();
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return "en";
        }

        private static string TryLookupLocalizedString(string table, string key)
        {
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key)) return null;

            string res = DirectLookup(table, key);
            if (IsValidTranslation(res)) return res;

            if (!table.StartsWith("ST_", StringComparison.OrdinalIgnoreCase))
            {
                res = DirectLookup("ST_" + table, key);
                if (IsValidTranslation(res)) return res;
            }
            else if (table.StartsWith("ST_", StringComparison.OrdinalIgnoreCase))
            {
                res = DirectLookup(table.Substring(3), key);
                if (IsValidTranslation(res)) return res;
            }

            return null;
        }

        private static string DirectLookup(string table, string key)
        {
            try
            {
                var locType = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
                if (locType == null) return null;

                var dbProp = locType.GetProperty("StringDatabase", BindingFlags.Public | BindingFlags.Static);
                var db = dbProp?.GetValue(null);
                if (db == null) return null;

                var methods = db.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.Name != "GetLocalizedString") continue;
                    var pars = m.GetParameters();
                    if (pars.Length >= 2)
                    {
                        object p0 = WrapTableReference(pars[0].ParameterType, table);
                        object p1 = WrapTableEntryReference(pars[1].ParameterType, key);
                        if (p0 == null || p1 == null) continue;

                        var args = new object[pars.Length];
                        args[0] = p0;
                        args[1] = p1;
                        for (int i = 2; i < pars.Length; i++)
                        {
                            if (pars[i].ParameterType.IsArray)
                                args[i] = Array.CreateInstance(pars[i].ParameterType.GetElementType(), 0);
                            else if (pars[i].DefaultValue != DBNull.Value)
                                args[i] = pars[i].DefaultValue;
                            else
                                args[i] = null;
                        }

                        var result = m.Invoke(db, args) as string;
                        if (IsValidTranslation(result))
                        {
                            return result;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static object WrapTableReference(Type targetType, string name)
        {
            if (targetType == typeof(string)) return name;
            var op = targetType.GetMethod("op_Implicit", new[] { typeof(string) });
            return op != null ? op.Invoke(null, new object[] { name }) : null;
        }

        private static object WrapTableEntryReference(Type targetType, string key)
        {
            if (targetType == typeof(string)) return key;
            var op = targetType.GetMethod("op_Implicit", new[] { typeof(string) });
            return op != null ? op.Invoke(null, new object[] { key }) : null;
        }

        private static bool IsValidTranslation(string text)
        {
            return !string.IsNullOrEmpty(text) && !text.StartsWith("No translation found for");
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }
    }
}
