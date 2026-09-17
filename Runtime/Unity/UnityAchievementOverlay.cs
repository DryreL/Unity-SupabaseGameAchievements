using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Visual for one toast. The default implementation builds a simple uGUI panel in code; subclass it
    /// (e.g. for TextMeshPro or a designed prefab) and assign the prefab to <see cref="UnityAchievementOverlay"/>.
    /// </summary>
    public class AchievementToastView : MonoBehaviour
    {
        [SerializeField] private RectTransform _panel;
        [SerializeField] private CanvasGroup _canvasGroup;
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
        public static AchievementToastView CreateDefault(Transform parent, int sortingOrder)
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
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            view._panel = CreateRect("Panel", root.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0), new Vector2(440, 104));
            var background = view._panel.gameObject.AddComponent<Image>();
            background.color = new Color(0.07f, 0.06f, 0.10f, 0.94f);
            background.raycastTarget = false;
            view._canvasGroup = view._panel.gameObject.AddComponent<CanvasGroup>();
            view._canvasGroup.interactable = false;
            view._canvasGroup.blocksRaycasts = false;

            var accent = CreateRect("Accent", view._panel, new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(4, 0));
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = new Color(0.91f, 0.23f, 0.44f, 1f);
            accentImage.raycastTarget = false;

            var icon = CreateRect("Icon", view._panel, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(72, 72));
            icon.anchoredPosition = new Vector2(20, 0);
            view._icon = icon.gameObject.AddComponent<Image>();
            view._icon.preserveAspect = true;
            view._icon.raycastTarget = false;

            view._header = CreateText("Header", view._panel, font, 13, FontStyle.Bold, new Color(0.91f, 0.23f, 0.44f), 14);
            view._title = CreateText("Title", view._panel, font, 20, FontStyle.Bold, Color.white, 34);
            view._description = CreateText("Description", view._panel, font, 15, FontStyle.Normal, new Color(0.79f, 0.78f, 0.84f), 60);

            view.SetMargin(new Vector2(24, 24));
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

        private static Text CreateText(string name, Transform parent, Font font, int size, FontStyle style, Color color, float top)
        {
            var rect = CreateRect(name, parent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1), new Vector2(-124, size + 10));
            rect.anchoredPosition = new Vector2(108, -top + 4);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
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

        [Header("Presentation")]
        [SerializeField] private AchievementToastView _toastPrefab;
        [SerializeField] private string _headerText = "ACHIEVEMENT UNLOCKED";
        [SerializeField] private Vector2 _margin = new Vector2(24, 24);
        [SerializeField] private int _sortingOrder = 32000;

        [Header("Timing (seconds)")]
        [SerializeField] private float _enterDuration = 0.25f;
        [SerializeField] private float _holdDuration = 2.25f;
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

        public void SetTimings(float enter, float hold, float exit, float localizationGrace)
        {
            _enterDuration = Mathf.Max(0f, enter);
            _holdDuration = Mathf.Max(0f, hold);
            _exitDuration = Mathf.Max(0f, exit);
            _localizationGrace = Mathf.Max(0f, localizationGrace);
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

            _view.SetContent(_headerText, _current.Title, _current.Description, icon);
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
                Debug.LogException(e); // GetIconAsync itself never throws; a faulted task would be a bug worth surfacing
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
                _view = AchievementToastView.CreateDefault(transform, _sortingOrder);
            }
            _view.SetMargin(_margin);
            _view.gameObject.SetActive(false);
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _phaseTime = 0f;
        }

        private static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t) * (1f - t); // ease-out cubic
        }
    }
}

