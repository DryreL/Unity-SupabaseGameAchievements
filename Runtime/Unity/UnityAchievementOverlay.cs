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
        private static Sprite _cachedGradientBg;

        public static Sprite GetOrCreateGradient(int width = 440, int height = 108, int radius = 14)
        {
            if (_cachedGradientBg != null) return _cachedGradientBg;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "AchievementToastGradientBg",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[width * height];

            // Start color: top-left (#14121D -> RGB: 20, 18, 29, 255)
            Color32 startColor = new Color32(20, 18, 29, 255);
            // End color: bottom-right (darker shade -> RGB: 10, 9, 15, 255)
            Color32 endColor = new Color32(10, 9, 15, 255);

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

                    // Signed distance field for rounded corner mask
                    float px = Mathf.Abs((x + 0.5f) - boxHalfW) - innerW;
                    float dx = Mathf.Max(0f, px);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float signedDist = dist - radius;

                    float alpha = Mathf.Clamp01(0.5f - signedDist);
                    byte a = (byte)Mathf.RoundToInt(alpha * 255f);

                    pixels[y * width + x] = new Color32(r, g, b, a);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.name = "AchievementToastGradientBg";
            _cachedGradientBg = sprite;
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

        public virtual void SetIcon(Sprite icon)
        {
            if (_icon == null) return;
            _icon.sprite = icon;
            _icon.enabled = icon != null;
        }

        public virtual void SetVisibility(float visibility)
        {
            if (_canvasGroup != null) _canvasGroup.alpha = visibility;
            if (_panel != null)
            {
                float hiddenDrop = _panel.rect.height + 24f;
                _panel.anchoredPosition = _restingPosition + new Vector2(0f, -(1f - visibility) * hiddenDrop);
            }
        }

        public virtual void SetMargin(Vector2 margin)
        {
            _restingPosition = new Vector2(-margin.x, margin.y);
            if (_panel != null) _panel.anchoredPosition = _restingPosition;
        }

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
            // Procedural gradient: starts at #14121D at top-left and smoothly darkens toward bottom-right, 100% opacity
            background.sprite = RoundedBoxSprite.GetOrCreateGradient(440, 108, cornerRadius);
            background.type = Image.Type.Simple;
            background.color = Color.white;
            background.raycastTarget = false;
            view._canvasGroup = view._panel.gameObject.AddComponent<CanvasGroup>();
            view._canvasGroup.interactable = false;
            view._canvasGroup.blocksRaycasts = false;

            Color effectiveAccentColor = accentColor ?? Color.white;

            var accent = CreateRect("Accent", view._panel, new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f), Vector2.zero);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = effectiveAccentColor;
            accentImage.raycastTarget = false;
            accent.gameObject.SetActive(false);
            view._accent = accent;

            var icon = CreateRect("Icon", view._panel, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, 72));
            icon.anchoredPosition = new Vector2(20, 0);
            view._icon = icon.gameObject.AddComponent<Image>();
            view._icon.preserveAspect = true;
            view._icon.raycastTarget = false;

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
        [Range(0, 30)]
        [SerializeField] private int _cornerRadius = 14;

        [Header("Timing (seconds)")]
        [SerializeField] private float _enterDuration = 0.25f;
        [SerializeField] private float _holdDuration = 4.5f;
        [SerializeField] private float _exitDuration = 0.5f;
        [SerializeField] private float _localizationGrace = 0.2f;

        [Header("Audio")]
        [SerializeField] private AudioClip _unlockSound;
        [SerializeField, Range(0f, 1f)] private float _volume = 0.8f;

        private AchievementNotificationService _service;
        private IAchievementIconProvider _icons;
        private IAchievementLocalizationProvider _localization;
        private AchievementToastView _view;
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
