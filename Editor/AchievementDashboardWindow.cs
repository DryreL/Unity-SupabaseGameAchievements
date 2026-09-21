using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// The achievements dashboard: create and edit every column of <c>public.achievements</c> in one place,
    /// keep the working copy saved in the project, push it to Supabase and write the game's manifest.
    /// </summary>
    /// <remarks>
    /// The working copy lives in a JSON file (<see cref="DefaultDataPath"/> unless changed) and is autosaved
    /// on every edit. Each entry remembers the server id Supabase gave it on the first push, so later edits
    /// PATCH that same row instead of inserting another one. <c>key</c> and <c>bit_index</c> are locked after
    /// the first push because the database rejects any change to them. Everything that writes to Supabase is
    /// Editor-only and needs a service/secret key (clients have no write grants, on purpose); the key is kept
    /// for the session and never written to disk. Hard deletes stay in the Manage Achievements window.
    /// </remarks>
    public sealed partial class AchievementDashboardWindow : EditorWindow
    {
        private const string PrefsPrefix = "DryreLHub.Achievements.Dashboard.";
        private const string DefaultDataPath = "ProjectSettings/DryreLHub.AchievementDashboard.json";
        private const double AutosaveDelaySeconds = 0.6;
        private const int IconCacheLimit = 256;

        private enum Filter { All, New, Modified, Synced, Hidden, Retired }

        // ---- state that survives across repaints --------------------------------------------------------
        private string _dataPath = DefaultDataPath;
        private string _supabaseUrl = "";
        private string _serviceKey = ""; // session-only, never persisted
        private DashboardData _data = new DashboardData();
        private string _loadError;

        private bool _saveDue;
        private double _saveAt;
        private string _saveError;
        private DateTime? _lastSaved;

        private Vector2 _scroll;
        private string _search = "";
        private Filter _filter = Filter.All;
        private bool _showConnection;
        private bool _autoFillAttempted;
        private bool _gameMissing;

        private string _status = "";
        private MessageType _statusType = MessageType.None;
        private bool _isBusy;

        private readonly Dictionary<string, UnityEngine.Object> _iconCache = new Dictionary<string, UnityEngine.Object>();
        private readonly HashSet<string> _iconLoading = new HashSet<string>();
        private readonly List<Texture2D> _ownedTextures = new List<Texture2D>();

        // ---- per-OnGUI scratch ----------------------------------------------------------------------------
        private Styles _styles;
        private Dictionary<DashboardAchievement, List<string>> _errors = new Dictionary<DashboardAchievement, List<string>>();

        [MenuItem("Tools/DryreL Hub/Supabase Game Achievements/Achievement Dashboard", false, 0)]
        private static void Open()
        {
            var window = GetWindow<AchievementDashboardWindow>(false, "Achievement Dashboard");
            window.minSize = new Vector2(620, 520);
            window.Show();
        }

        // =====================================================================================================
        // Lifecycle, persistence
        // =====================================================================================================

        private void OnEnable()
        {
            _supabaseUrl = EditorPrefs.GetString(PrefsPrefix + "SupabaseUrl", "");
            _dataPath = EditorPrefs.GetString(PrefsPrefix + "DataPath", DefaultDataPath);
            LoadDataFile();
            _showConnection = string.IsNullOrEmpty(_data.GameSlug) || _data.GameId <= 0;
        }

        private void OnDisable()
        {
            if (_saveDue) SaveNow();
            foreach (var texture in _ownedTextures)
                if (texture != null) DestroyImmediate(texture);
            _ownedTextures.Clear();
            _iconCache.Clear();
            _iconLoading.Clear();
        }

        private void OnFocus() => DropLocalIconCache();

        private void OnInspectorUpdate()
        {
            if (_saveDue && EditorApplication.timeSinceStartup >= _saveAt) SaveNow();
            if (_tab == Tab.Rules && EditorApplication.timeSinceStartup >= _nextBindingScan) RefreshBindingScan();
            Repaint(); // keeps the "saved at" label and download previews fresh
        }

        private void LoadDataFile()
        {
            _loadError = null;
            try
            {
                _data = DashboardData.Load(ToFullPath(_dataPath));
            }
            catch (Exception e)
            {
                // Never autosave over a file we could not read - that would destroy the only copy.
                _data = new DashboardData();
                _loadError = "Could not read '" + _dataPath + "': " + e.Message;
            }
            _saveDue = false;
            _rulesFileStateDirty = true;
            _drafts.Clear();
        }

        private void MarkDirty()
        {
            if (_loadError != null) return;
            _saveDue = true;
            _rulesFileStateDirty = true;
            _saveAt = EditorApplication.timeSinceStartup + AutosaveDelaySeconds;
        }

        private void SaveNow()
        {
            _saveDue = false;
            if (_loadError != null) return;
            try
            {
                _data.Save(ToFullPath(_dataPath));
                _saveError = null;
                _lastSaved = DateTime.Now;
            }
            catch (Exception e)
            {
                _saveError = e.Message;
            }
        }

        private void SwitchDataFile(string path)
        {
            if (_saveDue) SaveNow();
            _dataPath = path;
            EditorPrefs.SetString(PrefsPrefix + "DataPath", _dataPath);
            LoadDataFile();
            _gameMissing = false;
            _lastSaved = null;
            SetStatus(_loadError ?? "Opened '" + _dataPath + "' (" + _data.Achievements.Count + " achievement(s)).",
                _loadError != null ? MessageType.Error : MessageType.Info);
        }

        // =====================================================================================================
        // GUI
        // =====================================================================================================

        private void OnGUI()
        {
            _styles = _styles ?? new Styles();
            _errors = _data.Achievements.ToDictionary(a => a, a => DashboardValidator.Validate(a, _data.Achievements));

            DrawHeader();
            DrawTabs();

            if (_tab == Tab.Rules)
            {
                DrawRulesTab();
                return;
            }

            using (new EditorGUI.DisabledScope(_isBusy))
            {
                DrawStatTiles();
                DrawToolbar();
                DrawConnection();
            }

            if (_loadError != null) EditorGUILayout.HelpBox(_loadError + "\nChoose another data file, or fix/delete this one and press Reload. Nothing is saved until this is resolved.", MessageType.Error);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);

            using (new EditorGUI.DisabledScope(_isBusy))
            {
                DrawList();
            }
        }

        private void DrawHeader()
        {
            Rect r = GUILayoutUtility.GetRect(0, 58, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, Palette.Header);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2, r.width, 2), Palette.Accent);

            string game = string.IsNullOrEmpty(_data.GameSlug) ? "No game selected" : _data.GameSlug;
            string detail = _data.GameId > 0
                ? "game #" + _data.GameId + "   |   catalog v" + Mathf.Max(_data.CatalogVersion, 1)
                : "not connected to Supabase yet";

            GUI.Label(new Rect(r.x + 16, r.y + 7, r.width - 220, 26), "Achievements  -  " + game, _styles.HeaderTitle);
            GUI.Label(new Rect(r.x + 16, r.y + 33, r.width - 220, 18), detail, _styles.HeaderSub);

            string saveText;
            Color saveColor;
            if (_loadError != null) { saveText = "NOT SAVING"; saveColor = Palette.Danger; }
            else if (_saveError != null) { saveText = "SAVE FAILED"; saveColor = Palette.Danger; }
            else if (_saveDue) { saveText = "UNSAVED..."; saveColor = Palette.Modified; }
            else if (_lastSaved.HasValue) { saveText = "SAVED " + _lastSaved.Value.ToString("HH:mm:ss"); saveColor = Palette.Synced; }
            else { saveText = "AUTOSAVE ON"; saveColor = Palette.Retired; }
            DrawPill(new Rect(r.xMax - 148, r.y + 18, 132, 22), saveText, saveColor);

            if (_saveError != null) EditorGUILayout.HelpBox("Could not save '" + _dataPath + "': " + _saveError, MessageType.Error);
        }

        private void DrawStatTiles()
        {
            var items = _data.Achievements;
            var tiles = new[]
            {
                (Label: "Total", Value: items.Count, Color: Palette.Accent, Filter: Filter.All),
                (Label: "New", Value: items.Count(a => a.State == DashboardSyncState.New), Color: Palette.New, Filter: Filter.New),
                (Label: "Modified", Value: items.Count(a => a.State == DashboardSyncState.Modified), Color: Palette.Modified, Filter: Filter.Modified),
                (Label: "Synced", Value: items.Count(a => a.State == DashboardSyncState.Synced), Color: Palette.Synced, Filter: Filter.Synced),
                (Label: "Hidden", Value: items.Count(a => a.Hidden), Color: Palette.Hidden, Filter: Filter.Hidden),
                (Label: "Retired", Value: items.Count(a => a.Retired), Color: Palette.Retired, Filter: Filter.Retired),
            };

            Rect row = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            const float gap = 6f;
            float width = (row.width - 16 - gap * (tiles.Length - 1)) / tiles.Length;

            for (int i = 0; i < tiles.Length; i++)
            {
                var tile = tiles[i];
                var rect = new Rect(row.x + 8 + i * (width + gap), row.y + 6, width, 42);
                bool selected = _filter == tile.Filter;

                EditorGUI.DrawRect(rect, selected ? Palette.TileSelected : Palette.Tile);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 3), tile.Color);
                GUI.Label(new Rect(rect.x, rect.y + 5, rect.width, 22), tile.Value.ToString(), _styles.TileNumber);
                GUI.Label(new Rect(rect.x, rect.y + 25, rect.width, 14), tile.Label, _styles.TileLabel);

                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    _filter = tile.Filter;
                    Event.current.Use();
                    GUI.FocusControl(null);
                }
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(140), GUILayout.MaxWidth(280));
                _filter = (Filter)EditorGUILayout.EnumPopup(_filter, EditorStyles.toolbarPopup, GUILayout.Width(84));
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(!CanTalkToSupabase(out _) || string.IsNullOrEmpty(_data.GameSlug)))
                {
                    if (GUILayout.Button(new GUIContent("Pull", "Fetch this game's rows from Supabase. Entries with unsent local edits are kept."), EditorStyles.toolbarButton, GUILayout.Width(50)))
                        ConnectAndPull();
                }

                int pending = _data.Pending.Count() + (_data.IsGameIconPending ? 1 : 0);
                using (new EditorGUI.DisabledScope(pending == 0 || _data.GameId <= 0 || !CanTalkToSupabase(out _)))
                {
                    if (GUILayout.Button(new GUIContent("Push " + (pending > 0 ? "(" + pending + ")" : ""), "Send every new or modified achievement, and a changed icon style, to Supabase."), EditorStyles.toolbarButton, GUILayout.Width(74)))
                        PushItems(_data.Pending.ToList(), true);
                }

                using (new EditorGUI.DisabledScope(_data.Achievements.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("Localize", "Create the string table (if missing) and one <key>_title / <key>_description entry per achievement, unless overridden."), EditorStyles.toolbarButton, GUILayout.Width(66)))
                        CreateLocalizationTable();
                }

                using (new EditorGUI.DisabledScope(_data.GameId <= 0 || _data.Achievements.All(a => a.Id <= 0)))
                {
                    if (GUILayout.Button(new GUIContent("Manifest", "Write the manifest JSON the game ships, from this dashboard."), EditorStyles.toolbarButton, GUILayout.Width(70)))
                        GenerateManifest();
                }
            }
        }

        private void DrawConnection()
        {
            _showConnection = EditorGUILayout.Foldout(_showConnection, "Connection & files", true);
            if (!_showConnection) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                _supabaseUrl = EditorGUILayout.TextField(new GUIContent("Supabase URL", "e.g. https://xxxx.supabase.co"), _supabaseUrl);
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);

                if (!_autoFillAttempted && string.IsNullOrEmpty(_supabaseUrl))
                {
                    _autoFillAttempted = true;
                    if (PatreonConfigReflection.TryGetSupabaseCredentials(out var url, out _))
                    {
                        _supabaseUrl = url;
                        EditorPrefs.SetString(PrefsPrefix + "SupabaseUrl", _supabaseUrl);
                    }
                }

                _serviceKey = EditorGUILayout.PasswordField(
                    new GUIContent("Service Key", "A Supabase service_role/secret key. Required to read an inactive game and to write anything: clients have no write grants. Never saved to disk - re-enter it each session."),
                    _serviceKey);

                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.DisabledScope(_data.GameId > 0))
                    _data.GameSlug = EditorGUILayout.TextField(new GUIContent("Game Slug", "games.slug. Locked once linked: use another data file for another game."), _data.GameSlug);
                _data.GameName = EditorGUILayout.TextField(new GUIContent("Game Name", "Only used if the game has to be created."), _data.GameName);
                if (EditorGUI.EndChangeCheck()) MarkDirty();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    bool ready = CanTalkToSupabase(out _) && !string.IsNullOrEmpty(_data.GameSlug);
                    using (new EditorGUI.DisabledScope(!ready))
                    {
                        if (GUILayout.Button(_data.GameId > 0 ? "Reconnect & Pull" : "Connect & Pull", GUILayout.Width(140))) ConnectAndPull();
                    }
                    using (new EditorGUI.DisabledScope(!ready || !_gameMissing))
                    {
                        if (GUILayout.Button(new GUIContent("Create Game", "Only enabled after a lookup found no game with this slug."), GUILayout.Width(110))) CreateGame();
                    }
                }

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent("Data File", "Where this dashboard's working copy is saved (autosaved). Relative paths are inside the project."));
                    EditorGUILayout.SelectableLabel(_dataPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("Change...", GUILayout.Width(70)))
                    {
                        string full = ToFullPath(_dataPath);
                        string chosen = EditorUtility.SaveFilePanel("Dashboard data file (an existing file is opened, not overwritten)",
                            Path.GetDirectoryName(full), Path.GetFileName(full), "json");
                        if (!string.IsNullOrEmpty(chosen)) SwitchDataFile(ToProjectRelativeIfPossible(chosen));
                    }
                    if (GUILayout.Button("Reload", GUILayout.Width(60))) SwitchDataFile(_dataPath);
                    if (GUILayout.Button("Reveal", GUILayout.Width(60))) EditorUtility.RevealInFinder(ToFullPath(_dataPath));
                }

                EditorGUI.BeginChangeCheck();
                string iconFolder = EditorGUILayout.TextField(
                    new GUIContent("Icon Folder", "New achievements get icon_path = <folder>/<key>. Relative to the runtime's icon prefix (Achievements/ in the bundled bootstraps), so the default 'images' means Assets/Resources/Achievements/images/<key>.png. Rename it freely; icon paths of never-pushed achievements follow."),
                    _data.IconFolder);
                if (EditorGUI.EndChangeCheck())
                {
                    _data.SetIconFolder(iconFolder);
                    MarkDirty();
                }

                EditorGUI.BeginChangeCheck();
                int styleIndex = EditorGUILayout.Popup(
                    new GUIContent("Icon Style", "Combined: each achievement's image already includes its background. Layered: one shared background image for every achievement, with the achievement's own icon drawn on top."),
                    (int)_data.IconStyle, new[] { "Combined  (background + icon in one image)", "Layered  (shared background + separate icon)" });
                if (styleIndex == (int)AchievementIconStyle.Layered)
                {
                    _data.IconBackground = HintTextField(
                        new GUIContent("Icon Background", "The shared background image, relative to the icon prefix, no extension. Empty = <Icon Folder>/background, i.e. Assets/Resources/Achievements/images/background.png."),
                        _data.IconBackground, _data.IconPathFor("background"));
                    _data.IconInset = EditorGUILayout.Slider(
                        new GUIContent("Icon Inset", "Margin around the icon inside the background, as a fraction of its size."),
                        _data.IconInset, 0f, 0.45f);
                }
                if (_data.IsGameIconPending)
                    EditorGUILayout.LabelField("Icon style change not sent yet: Push saves it to Supabase (games.icon_style), so every export carries it.", _styles.Mini);
                if (EditorGUI.EndChangeCheck())
                {
                    _data.IconStyle = (AchievementIconStyle)styleIndex;
                    DropLocalIconCache(); // previews are cheap to reload and may have changed on disk
                    MarkDirty();
                }

                EditorGUI.BeginChangeCheck();
                _data.DefaultLocalizationTable = EditorGUILayout.TextField(
                    new GUIContent("Localization Table", "Default string table for achievement text (also the collection the Localize button creates). Individual achievements can override it."),
                    _data.DefaultLocalizationTable);
                _data.LocalizationFolder = EditorGUILayout.TextField(
                    new GUIContent("Localization Folder", "Where Localize creates the table's assets: <folder>/<table>/. Must be inside Assets."),
                    _data.LocalizationFolder);
                if (EditorGUI.EndChangeCheck()) MarkDirty();
                if (!AchievementLocalizationBridge.IsAvailable)
                    EditorGUILayout.LabelField("Unity Localization (com.unity.localization) is not installed: the Localize button is unavailable.", _styles.Mini);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _data.ManifestPath = EditorGUILayout.TextField(new GUIContent("Manifest Path", "Where Manifest writes achievements.json. The default matches PatreonAchievementsBootstrap's Manifest Resource Path."), _data.ManifestPath);
                    if (EditorGUI.EndChangeCheck()) MarkDirty();
                    if (GUILayout.Button("Browse...", GUILayout.Width(70)))
                    {
                        string full = ToFullPath(string.IsNullOrEmpty(_data.ManifestPath) ? DashboardData.DefaultManifestPath : _data.ManifestPath);
                        string chosen = EditorUtility.SaveFilePanel("Manifest output", Path.GetDirectoryName(full), "achievements", "json");
                        if (!string.IsNullOrEmpty(chosen))
                        {
                            _data.ManifestPath = ToProjectRelativeIfPossible(chosen);
                            MarkDirty();
                        }
                    }
                }
            }
        }

        private void DrawList()
        {
            var visible = _data.Achievements
                .Where(MatchesFilter)
                .Where(MatchesSearch)
                .OrderBy(a => a.BitIndex)
                .ToList();

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                if (_data.Achievements.Count == 0)
                {
                    EditorGUILayout.Space(24);
                    GUILayout.Label("No achievements yet.", _styles.EmptyTitle);
                    GUILayout.Label("Press + to add one, or Connect & Pull to load an existing game.", _styles.EmptySub);
                }
                else if (visible.Count == 0)
                {
                    EditorGUILayout.Space(24);
                    GUILayout.Label("Nothing matches the current search/filter.", _styles.EmptySub);
                }

                DashboardAchievement toRemove = null;
                EditorGUI.BeginChangeCheck();
                foreach (var achievement in visible)
                    if (DrawCard(achievement)) toRemove = achievement;
                if (EditorGUI.EndChangeCheck()) MarkDirty();

                if (toRemove != null && EditorUtility.DisplayDialog("Remove from dashboard",
                        "Remove '" + toRemove.Key + "'? It was never sent to Supabase, so this only discards the local draft.", "Remove", "Cancel"))
                {
                    _data.Achievements.Remove(toRemove);
                    MarkDirty();
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.Space(6);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    var previous = GUI.backgroundColor;
                    GUI.backgroundColor = Palette.Accent;
                    if (GUILayout.Button(new GUIContent("+   Add Achievement", "Adds an achievement with the next free bit index."), _styles.AddButton, GUILayout.Height(34)))
                    {
                        AddAchievement();
                        GUIUtility.ExitGUI();
                    }
                    GUI.backgroundColor = previous;
                    GUILayout.Space(8);
                }
                EditorGUILayout.Space(12);
            }
        }

        private void AddAchievement()
        {
            if (!string.IsNullOrEmpty(_search) || _filter != Filter.All)
            {
                _search = "";
                _filter = Filter.All; // otherwise the new card would be filtered out of sight
            }
            _data.AddNew();
            _scroll.y = float.MaxValue;
            MarkDirty();
            GUI.FocusControl(null);
        }

        /// <returns>True when the card's remove button was pressed.</returns>
        private bool DrawCard(DashboardAchievement a)
        {
            var errors = _errors.TryGetValue(a, out var found) ? found : new List<string>();
            Color accent = AccentFor(a);
            bool remove = false;

            Rect card = EditorGUILayout.BeginVertical(_styles.Card);

            Rect header = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            float right = header.xMax;

            // Controls first: IMGUI hands a click to the first control that claims it, so the buttons must
            // be declared before the full-width "toggle expand" button that sits underneath them.
            if (a.State == DashboardSyncState.New)
            {
                right -= 28;
                if (GUI.Button(new Rect(right, header.y + 8, 24, 22), new GUIContent("x", "Remove this draft"), EditorStyles.miniButton)) remove = true;
                right -= 4;
            }

            right -= 54;
            using (new EditorGUI.DisabledScope(a.State == DashboardSyncState.Synced || _data.GameId <= 0 || !CanTalkToSupabase(out _)))
            {
                if (GUI.Button(new Rect(right, header.y + 8, 54, 22), new GUIContent("Push", "Send only this achievement to Supabase."), EditorStyles.miniButton))
                    PushItems(new List<DashboardAchievement> { a });
            }
            right -= 6;

            right -= 66;
            DrawPill(new Rect(right, header.y + 10, 66, 18), a.State.ToString().ToUpperInvariant(), accent);
            right -= 6;

            right -= 44;
            GUI.Label(new Rect(right, header.y + 10, 44, 18), "bit " + a.BitIndex, _styles.Mini);

            a.Expanded = EditorGUI.Foldout(new Rect(header.x, header.y + 11, 14, 16), a.Expanded, GUIContent.none, false);
            if (GUI.Button(new Rect(header.x + 14, header.y, Mathf.Max(0, right - header.x - 14), header.height), GUIContent.none, GUIStyle.none))
            {
                a.Expanded = !a.Expanded;
                GUI.FocusControl(null);
            }

            if (Event.current.type == EventType.Repaint)
            {
                DrawIcon(new Rect(header.x + 20, header.y + 3, 32, 32), a);

                string title = string.IsNullOrEmpty(a.Title) ? "(untitled)" : a.Title;
                float textWidth = Mathf.Max(0, right - header.x - 62);
                GUI.Label(new Rect(header.x + 60, header.y + 2, textWidth, 20), title, _styles.CardTitle);

                string sub = string.IsNullOrEmpty(a.Key) ? "(no key)" : a.Key;
                if (a.Hidden) sub += "   |   hidden";
                if (a.Retired) sub += "   |   retired";
                if (errors.Count > 0) sub += "   |   needs attention";
                GUI.Label(new Rect(header.x + 60, header.y + 20, textWidth, 16), sub, errors.Count > 0 ? _styles.MiniDanger : _styles.Mini);
            }

            if (a.Expanded) DrawCardBody(a, errors);

            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(card.x + 1, card.y + 1, 4, card.height - 2), accent);

            return remove;
        }

        private void DrawCardBody(DashboardAchievement a, List<string> errors)
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 118;

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Identity", _styles.Section);

            if (a.IsIdentityLocked)
            {
                EditorGUILayout.LabelField(new GUIContent("Key", "Immutable once the achievement exists on the server."), new GUIContent(a.Key));
                EditorGUILayout.LabelField(new GUIContent("Bit Index", "Immutable and never reused."), new GUIContent(a.BitIndex.ToString()));
                EditorGUILayout.LabelField("Server ID", a.Id.ToString());
            }
            else
            {
                string newKey = EditorGUILayout.TextField(new GUIContent("Key", "Stable gameplay identifier, e.g. first_blood. Locked after the first push."), a.Key);
                if (newKey != a.Key) _data.RenameKey(a, newKey);
                a.BitIndex = EditorGUILayout.DelayedIntField(new GUIContent("Bit Index", "Position in the local unlock bitset. Locked after the first push and never reused."), a.BitIndex);
                EditorGUILayout.LabelField("Server ID", "assigned on the first push");
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Content", _styles.Section);
            a.Title = EditorGUILayout.TextField(new GUIContent("Title", "Fallback title (1-200 characters)."), a.Title);
            EditorGUILayout.LabelField(new GUIContent("Description", "Fallback description (up to 1000 characters)."));
            a.Description = EditorGUILayout.TextArea(a.Description, _styles.WrapArea, GUILayout.MinHeight(50));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Icon", _styles.Section);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    a.IconPath = EditorGUILayout.TextField(new GUIContent("Icon Path", "Relative to the icon prefix, no extension, e.g. images/first_blood - or a full URL. Filled from the Icon Folder setting."), a.IconPath);
                    a.IconUrl = EditorGUILayout.TextField(new GUIContent("Icon URL", "Optional. Downloaded first; falls back to Icon Path on any failure."), a.IconUrl);
                }
                Rect preview = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
                if (Event.current.type == EventType.Repaint) DrawIcon(preview, a);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Behavior", _styles.Section);
            a.Hidden = EditorGUILayout.Toggle(new GUIContent("Hidden", "Hidden until unlocked."), a.Hidden);
            a.Retired = EditorGUILayout.Toggle(new GUIContent("Retired", "Retired achievements can no longer be unlocked; the bit index stays reserved."), a.Retired);
            a.DisplayOrder = EditorGUILayout.IntField(new GUIContent("Display Order", "Sorts before bit index; 0 keeps bit order."), a.DisplayOrder);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Localization (optional)", _styles.Section);
            EditorGUILayout.LabelField("Leave empty to use the defaults shown in grey; the Localize button fills them in.", _styles.Mini);
            a.LocalizationTable = HintTextField(new GUIContent("Table", "Unity Localization string table. Empty = the default table from Connection & files."),
                a.LocalizationTable, _data.DefaultLocalizationTable);
            a.TitleKey = HintTextField(new GUIContent("Title Key", "Entry key for the title. Empty = <key>_title."),
                a.TitleKey, DashboardData.DefaultTitleKey(a.Key));
            a.DescriptionKey = HintTextField(new GUIContent("Description Key", "Entry key for the description. Empty = <key>_description."),
                a.DescriptionKey, DashboardData.DefaultDescriptionKey(a.Key));

            EditorGUIUtility.labelWidth = previousLabelWidth;

            if (errors.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(string.Join("\n", errors), MessageType.Error);
            }
        }

        private bool MatchesFilter(DashboardAchievement a)
        {
            switch (_filter)
            {
                case Filter.New: return a.State == DashboardSyncState.New;
                case Filter.Modified: return a.State == DashboardSyncState.Modified;
                case Filter.Synced: return a.State == DashboardSyncState.Synced;
                case Filter.Hidden: return a.Hidden;
                case Filter.Retired: return a.Retired;
                default: return true;
            }
        }

        private bool MatchesSearch(DashboardAchievement a)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            string needle = _search.Trim();
            return Contains(a.Key, needle) || Contains(a.Title, needle) || Contains(a.Description, needle);
        }

        /// <summary>A text field that shows a grey default while it is empty (an empty value means "use the default").</summary>
        private string HintTextField(GUIContent label, string value, string hint)
        {
            Rect rect = EditorGUILayout.GetControlRect();
            value = EditorGUI.TextField(rect, label, value);
            if (string.IsNullOrEmpty(value) && Event.current.type == EventType.Repaint)
            {
                float offset = EditorGUIUtility.labelWidth + 4;
                GUI.Label(new Rect(rect.x + offset, rect.y, Mathf.Max(0, rect.width - offset), rect.height), hint, _styles.Hint);
            }
            return value;
        }

        private static bool Contains(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static Color AccentFor(DashboardAchievement a)
        {
            if (a.Retired) return Palette.Retired;
            switch (a.State)
            {
                case DashboardSyncState.New: return Palette.New;
                case DashboardSyncState.Modified: return Palette.Modified;
                default: return Palette.Synced;
            }
        }

        private void DrawPill(Rect rect, string text, Color color)
        {
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, text, _styles.Pill);
        }

        // =====================================================================================================
        // Icon previews: icon_url first, then icon_path (the runtime's priority), placeholder otherwise.
        // =====================================================================================================

        private void DrawIcon(Rect rect, DashboardAchievement a)
        {
            EditorGUI.DrawRect(rect, Palette.Tile);
            Rect iconRect = Inset(rect);

            if (_data.IconStyle == AchievementIconStyle.Layered)
            {
                // Same composition as the runtime: the shared background fills the slot, the icon sits inside it.
                DrawImage(Inset(rect), GetLocalIcon(_data.EffectiveIconBackground));
                float margin = rect.width * Mathf.Clamp(_data.IconInset, 0f, 0.45f);
                iconRect = new Rect(rect.x + margin, rect.y + margin, rect.width - 2 * margin, rect.height - 2 * margin);
            }

            if (!DrawImage(iconRect, ResolveIcon(a)))
                GUI.Label(rect, "?", _styles.IconPlaceholder);
        }

        private static bool DrawImage(Rect rect, UnityEngine.Object image)
        {
            if (image is Sprite sprite && sprite.texture != null)
            {
                var texture = sprite.texture;
                var uv = new Rect(sprite.rect.x / texture.width, sprite.rect.y / texture.height,
                    sprite.rect.width / texture.width, sprite.rect.height / texture.height);
                GUI.DrawTextureWithTexCoords(rect, texture, uv);
                return true;
            }
            if (image is Texture texture2)
            {
                GUI.DrawTexture(rect, texture2, ScaleMode.ScaleToFit);
                return true;
            }
            return false;
        }

        private static Rect Inset(Rect r) => new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4);

        private UnityEngine.Object ResolveIcon(DashboardAchievement a)
        {
            string remote = !string.IsNullOrEmpty(a.IconUrl) ? a.IconUrl
                : !string.IsNullOrEmpty(a.IconPath) && DashboardValidator.HasWebPrefix(a.IconPath) ? a.IconPath : null;

            if (remote != null)
            {
                var downloaded = GetRemoteIcon(NormalizeUrl(remote));
                if (downloaded != null) return downloaded;
            }

            if (!string.IsNullOrEmpty(a.IconPath) && !DashboardValidator.HasWebPrefix(a.IconPath)) return GetLocalIcon(a.IconPath);
            return null;
        }

        private UnityEngine.Object GetLocalIcon(string resourcePath)
        {
            string key = "local:" + resourcePath;
            if (_iconCache.TryGetValue(key, out var cached)) return cached;
            if (_iconCache.Count > IconCacheLimit) DropLocalIconCache();

            int dot = resourcePath.LastIndexOf('.');
            int slash = Math.Max(resourcePath.LastIndexOf('/'), resourcePath.LastIndexOf('\\'));
            string withoutExtension = dot > slash ? resourcePath.Substring(0, dot) : resourcePath;

            // icon_path is relative to the runtime's icon prefix: "Achievements/" in the bundled bootstraps, empty otherwise.
            UnityEngine.Object loaded = null;
            foreach (string candidate in new[] { "Achievements/" + withoutExtension, withoutExtension })
            {
                loaded = Resources.Load<Sprite>(candidate);
                if (loaded == null) loaded = Resources.Load<Texture2D>(candidate);
                if (loaded != null) break;
            }
            _iconCache[key] = loaded;
            return loaded;
        }

        private void DropLocalIconCache()
        {
            foreach (var key in _iconCache.Keys.Where(k => k.StartsWith("local:", StringComparison.Ordinal)).ToList())
                _iconCache.Remove(key);
        }

        private UnityEngine.Object GetRemoteIcon(string url)
        {
            if (_iconCache.TryGetValue(url, out var cached)) return cached;
            if (_iconLoading.Contains(url)) return null;

            // Only ever fetch http(s): file:// and friends have no business in an icon field.
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _iconCache[url] = null;
                return null;
            }

            _iconLoading.Add(url);
            var request = UnityWebRequestTexture.GetTexture(url);
            request.SendWebRequest().completed += _ =>
            {
                Texture2D texture = null;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    texture = DownloadHandlerTexture.GetContent(request);
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    if (this != null) _ownedTextures.Add(texture);
                    else
                    {
                        DestroyImmediate(texture); // window closed mid-download: nothing will clean this up later
                        texture = null;
                    }
                }
                request.Dispose();
                _iconLoading.Remove(url);
                _iconCache[url] = texture;
                if (this != null) Repaint();
            };
            return null;
        }

        private static string NormalizeUrl(string url) =>
            url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;

        // =====================================================================================================
        // Supabase: connect, pull, push, manifest
        // =====================================================================================================

        private bool CanTalkToSupabase(out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(_supabaseUrl)) problem = "Enter the Supabase URL.";
            else if (string.IsNullOrEmpty(_serviceKey)) problem = "Enter a service/secret key (it is never saved to disk).";
            return problem == null;
        }

        private void ConnectAndPull()
        {
            if (!CanTalkToSupabase(out string problem)) { Fail(problem); return; }
            string slugProblem = DashboardValidator.ValidateGame(_data.GameSlug, _data.GameName);
            if (slugProblem != null) { Fail(slugProblem); return; }

            _isBusy = true;
            SetStatus("Looking up game '" + _data.GameSlug + "'...", MessageType.Info);
            LookUpGame(true, response =>
            {
                if (!response.Ok) { Fail(Describe(response)); return; }

                var games = JArray.Parse(response.Body);
                if (games.Count == 0)
                {
                    _gameMissing = true;
                    _isBusy = false;
                    SetStatus("Game '" + _data.GameSlug + "' does not exist in Supabase yet. Press Create Game to add it.", MessageType.Warning);
                    return;
                }

                _gameMissing = false;
                ApplyGameRow((JObject)games[0]);
                PullAchievements();
            });
        }

        // The icon_* columns come from 20260921000000_games_icon_style.sql; a database without it answers 400
        // for them, so retry once without them and keep working with the Combined default.
        private void LookUpGame(bool withIconColumns, Action<Response> done)
        {
            string columns = "id,slug,name,catalog_version" + (withIconColumns ? ",icon_style,icon_background,icon_inset" : "");
            Send("GET", "games?slug=eq." + UnityWebRequest.EscapeURL(_data.GameSlug) + "&select=" + columns, null, null, response =>
            {
                if (!response.Ok && withIconColumns && response.Code == 400 && (response.Body ?? "").Contains("icon_"))
                {
                    LookUpGame(false, done);
                    return;
                }
                done(response);
            });
        }

        private void CreateGame()
        {
            string problem = DashboardValidator.ValidateGame(_data.GameSlug, _data.GameName);
            if (problem != null) { Fail(problem); return; }

            string name = string.IsNullOrWhiteSpace(_data.GameName) ? _data.GameSlug : _data.GameName.Trim();
            if (!EditorUtility.DisplayDialog("Create game",
                    "Create the game '" + name + "' (slug '" + _data.GameSlug + "') in Supabase?", "Create", "Cancel")) return;

            _isBusy = true;
            SetStatus("Creating game '" + _data.GameSlug + "'...", MessageType.Info);
            var body = new JObject { ["slug"] = _data.GameSlug, ["name"] = name };
            Send("POST", "games", body.ToString(Newtonsoft.Json.Formatting.None), "return=representation", response =>
            {
                if (!response.Ok) { Fail(Describe(response)); return; }

                _gameMissing = false;
                ApplyGameRow((JObject)JArray.Parse(response.Body)[0]);
                SaveNow();
                _isBusy = false;
                SetStatus("Created game '" + _data.GameSlug + "' (id " + _data.GameId + "). It is inactive until you set is_active = true.", MessageType.Info);
            });
        }

        private void ApplyGameRow(JObject game)
        {
            _data.GameId = game.Value<long>("id");
            _data.CatalogVersion = game.Value<int?>("catalog_version") ?? 1;
            string name = game.Value<string>("name");
            if (string.IsNullOrEmpty(_data.GameName) && !string.IsNullOrEmpty(name)) _data.GameName = name;
            _gameIconEditsKept = !_data.ApplyGameIcon(game);
            SaveNow();
        }

        private bool _gameIconEditsKept;

        private void PullAchievements()
        {
            SetStatus("Fetching achievements...", MessageType.Info);
            const string columns = "id,achievement_key,bit_index,title,description,icon_path,icon_url,hidden,is_retired,display_order,localization_table,title_key,description_key";
            Send("GET", "achievements?game_id=eq." + _data.GameId + "&select=" + columns + "&order=bit_index.asc", null, null, response =>
            {
                if (!response.Ok) { Fail(Describe(response)); return; }

                var result = _data.MergeRemote(JArray.Parse(response.Body));
                SaveNow();
                _isBusy = false;

                string message = "Pulled '" + _data.GameSlug + "' (catalog v" + _data.CatalogVersion + "): " +
                    result.Added + " added, " + result.Updated + " updated";
                if (result.LocalEditsKept > 0) message += ", " + result.LocalEditsKept + " kept with your unsent edits";
                if (_gameIconEditsKept) message += "; your unsent icon style change was kept";
                SetStatus(message + ".", MessageType.Info);
                _showConnection = false;
            });
        }

        /// <param name="includeGame">Also send a changed game-wide icon style (the toolbar Push does, a single card's does not).</param>
        private void PushItems(List<DashboardAchievement> items, bool includeGame = false)
        {
            bool sendGame = includeGame && _data.IsGameIconPending;
            if (items.Count == 0 && !sendGame) return;
            if (!CanTalkToSupabase(out string problem)) { Fail(problem); return; }
            if (_data.GameId <= 0) { Fail("Connect the game first (Connection & files > Connect & Pull)."); return; }

            var invalid = items.Where(a => _errors.TryGetValue(a, out var list) && list.Count > 0).ToList();
            if (invalid.Count > 0)
            {
                foreach (var a in invalid) a.Expanded = true;
                Fail("Fix " + invalid.Count + " achievement(s) before pushing: '" + invalid[0].Key + "' - " + _errors[invalid[0]][0]);
                return;
            }

            var inserts = items.Where(a => a.State == DashboardSyncState.New).ToList();
            var updates = items.Where(a => a.State == DashboardSyncState.Modified).ToList();
            if (inserts.Count + updates.Count == 0 && !sendGame) return;

            _isBusy = true;
            SetStatus("Pushing " + (inserts.Count + updates.Count) + " achievement(s)" + (sendGame ? " and the icon style" : "") + "...", MessageType.Info);
            RunInserts(inserts, () => RunUpdates(updates, 0, () => RunGameIcon(sendGame, () => RefreshCatalogVersion(() =>
            {
                _isBusy = false;
                SetStatus("Pushed " + inserts.Count + " new and " + updates.Count + " updated achievement(s)" + (sendGame ? " and the icon style" : "") +
                    ". Catalog is now v" + _data.CatalogVersion + ".", MessageType.Info);
            }))));
        }

        // The icon style belongs to the game row, not to an achievement: PATCH games, and the server bumps catalog_version.
        private void RunGameIcon(bool send, Action next)
        {
            if (!send) { next(); return; }

            Send("PATCH", "games?id=eq." + _data.GameId, _data.BuildGameIconPayload().ToString(Newtonsoft.Json.Formatting.None), "return=representation", response =>
            {
                if (!response.Ok) { Fail("Icon style: " + Describe(response)); return; }
                if (JArray.Parse(response.Body).Count == 0) { Fail("The game (id " + _data.GameId + ") no longer exists in Supabase. Press Pull to resync."); return; }

                _data.MarkGameIconSynced();
                SaveNow();
                next();
            });
        }

        private void RunInserts(List<DashboardAchievement> inserts, Action next)
        {
            if (inserts.Count == 0) { next(); return; }

            // One bulk POST is one transaction: either every new row lands or none does.
            var body = new JArray(inserts.Select(a => a.BuildInsertPayload(_data.GameId)));
            Send("POST", "achievements", body.ToString(Newtonsoft.Json.Formatting.None), "return=representation", response =>
            {
                if (!response.Ok) { Fail(Describe(response)); return; }

                var rows = JArray.Parse(response.Body).Cast<JObject>().ToList();
                foreach (var a in inserts)
                {
                    var row = rows.FirstOrDefault(r => r.Value<string>("achievement_key") == a.Key);
                    if (row == null) { Fail("Supabase did not return the row for '" + a.Key + "'. Press Pull to resync."); return; }
                    a.ApplyRow(row);
                }
                SaveNow(); // the new server ids must reach disk before anything else can fail
                next();
            });
        }

        private void RunUpdates(List<DashboardAchievement> updates, int index, Action next)
        {
            if (index >= updates.Count) { next(); return; }

            var a = updates[index];
            Send("PATCH", "achievements?id=eq." + a.Id, a.BuildUpdatePayload().ToString(Newtonsoft.Json.Formatting.None), "return=representation", response =>
            {
                if (!response.Ok) { Fail("'" + a.Key + "': " + Describe(response)); return; }

                var rows = JArray.Parse(response.Body);
                if (rows.Count == 0)
                {
                    Fail("'" + a.Key + "' (id " + a.Id + ") no longer exists in Supabase. Press Pull to resync.");
                    return;
                }
                a.ApplyRow((JObject)rows[0]);
                SaveNow();
                RunUpdates(updates, index + 1, next);
            });
        }

        // Every insert/update bumps games.catalog_version on the server; the manifest has to carry the new one.
        private void RefreshCatalogVersion(Action next)
        {
            Send("GET", "games?id=eq." + _data.GameId + "&select=id,catalog_version", null, null, response =>
            {
                if (!response.Ok) { Fail("Pushed, but could not read the new catalog version: " + Describe(response)); return; }

                var games = JArray.Parse(response.Body);
                if (games.Count == 1) _data.CatalogVersion = games[0].Value<int>("catalog_version");
                SaveNow();
                next();
            });
        }

        // =====================================================================================================
        // Localization: string table + entries, and the matching achievement fields
        // =====================================================================================================

        private void CreateLocalizationTable()
        {
            if (!AchievementLocalizationBridge.IsAvailable)
            {
                Fail("Unity Localization is not installed in this project. Install the 'Localization' package (com.unity.localization) from the Package Manager, then try again.");
                return;
            }

            string problem = DashboardValidator.ValidateLocalizationSetup(_data.DefaultLocalizationTable, _data.LocalizationFolder);
            if (problem != null) { Fail(problem); return; }

            var plan = _data.BuildLocalizationPlan();
            if (plan.Count == 0) { Fail("Add an achievement with a key first."); return; }

            var tables = plan.Select(p => p.Table).Distinct().ToList();
            var invalidTable = tables.FirstOrDefault(t => DashboardValidator.ValidateLocalizationSetup(t, _data.LocalizationFolder) != null);
            if (invalidTable != null) { Fail("An achievement uses the invalid table name '" + invalidTable + "'."); return; }

            string folders = string.Join(", ", tables.Select(t => _data.LocalizationFolder.TrimEnd('/', '\\') + "/" + t));
            if (!EditorUtility.DisplayDialog("Create localization table",
                    "This adds " + plan.Count + " entr" + (plan.Count == 1 ? "y" : "ies") + " to " + string.Join(", ", tables) +
                    " (creating the table in " + folders + " if it does not exist) and fills the empty Table / Title Key / Description Key fields of your achievements.\n\n" +
                    "Existing entries are never overwritten.",
                    "Create", "Cancel")) return;

            LocalizationSyncResult result;
            try
            {
                result = AchievementLocalizationBridge.Sync(_data.LocalizationFolder, plan);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail("Could not update the string table: " + e.Message);
                return;
            }

            if (!result.Ok) { Fail(result.Message); return; }

            int updated = _data.ApplyLocalizationDefaults();
            MarkDirty();

            var message = new StringBuilder();
            if (result.TablesCreated > 0) message.Append("Created " + result.TablesCreated + " string table(s) with " + result.Locales + " locale(s). ");
            message.Append(result.EntriesAdded + " entr" + (result.EntriesAdded == 1 ? "y" : "ies") + " added, " + result.EntriesKept + " already existed (left untouched). ");
            message.Append("The new entries hold each achievement's current text in every locale: replace it with translations. ");
            if (updated > 0) message.Append(updated + " achievement(s) now reference the table: push them to Supabase, then regenerate the manifest.");
            SetStatus(message.ToString().TrimEnd(), MessageType.Info);
        }

        private void GenerateManifest()
        {
            if (_data.GameId <= 0 || _data.CatalogVersion < 1) { Fail("Connect the game first so the manifest knows its game id and catalog version."); return; }
            if (string.IsNullOrWhiteSpace(_data.ManifestPath)) { Fail("Set a manifest path under Connection & files."); return; }

            int unsent = _data.Achievements.Count(a => a.State == DashboardSyncState.New);
            int modified = _data.Achievements.Count(a => a.State == DashboardSyncState.Modified);
            bool iconPending = _data.IsGameIconPending;
            if ((unsent + modified > 0 || iconPending) && !EditorUtility.DisplayDialog("Unsent changes",
                    unsent + " new achievement(s) will be left out (no server id yet) and " + modified +
                    " modified one(s) carry edits Supabase does not have yet." + (iconPending ? "\nThe icon style change is not in Supabase yet either." : "") +
                    "\n\nPush first for a manifest that matches the server.",
                    "Generate anyway", "Cancel")) return;

            try
            {
                string json = DashboardData.SerializeManifest(_data.BuildManifest());
                string fullPath = ToFullPath(_data.ManifestPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, json, new UTF8Encoding(false));

                string relative = ToProjectRelativeIfPossible(fullPath);
                if (relative.StartsWith("Assets/", StringComparison.Ordinal)) AssetDatabase.ImportAsset(relative);

                int count = _data.Achievements.Count(a => a.Id > 0);
                SetStatus("Wrote " + count + " achievement(s) (catalog v" + _data.CatalogVersion + ") to " + _data.ManifestPath + "." +
                    (unsent + modified > 0 || iconPending ? " Some local changes are not in Supabase yet." : ""), unsent + modified > 0 || iconPending ? MessageType.Warning : MessageType.Info);
            }
            catch (Exception e)
            {
                Fail("Could not write the manifest: " + e.Message);
            }
        }

        // =====================================================================================================
        // HTTP
        // =====================================================================================================

        private struct Response
        {
            public bool Ok;
            public long Code;
            public string Body;
            public string Error;
        }

        private void Send(string method, string path, string body, string prefer, Action<Response> done)
        {
            UnityWebRequest request = null;
            try
            {
                request = new UnityWebRequest(_supabaseUrl.TrimEnd('/') + "/rest/v1/" + path, method)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                if (!string.IsNullOrEmpty(body)) request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));

                request.SetRequestHeader("apikey", _serviceKey);
                // New-style sb_secret_ keys are not JWTs and must only travel in apikey; legacy JWT keys also go in Authorization.
                if (_serviceKey.StartsWith("eyJ", StringComparison.Ordinal)) request.SetRequestHeader("Authorization", "Bearer " + _serviceKey);
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                if (!string.IsNullOrEmpty(prefer)) request.SetRequestHeader("Prefer", prefer);

                var sent = request;
                sent.SendWebRequest().completed += _ =>
                {
                    var response = new Response
                    {
                        Ok = sent.result == UnityWebRequest.Result.Success,
                        Code = sent.responseCode,
                        Body = sent.downloadHandler != null ? sent.downloadHandler.text : null,
                        Error = sent.error,
                    };
                    sent.Dispose();

                    try
                    {
                        done(response);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        Fail("Unexpected response from Supabase: " + e.Message);
                    }
                };
            }
            catch (Exception e)
            {
                // e.g. a malformed URL: surfaced as a normal failure instead of leaving the window stuck busy.
                request?.Dispose();
                Fail("Could not send the request: " + e.Message);
            }
        }

        private static string Describe(Response response)
        {
            string detail = null;
            try
            {
                var error = JObject.Parse(response.Body ?? "");
                detail = string.Join(" ", new[] { error.Value<string>("message"), error.Value<string>("details"), error.Value<string>("hint") }
                    .Where(part => !string.IsNullOrEmpty(part)));
            }
            catch (Exception)
            {
                // Not a PostgREST error body (e.g. a network failure): fall through to the raw text.
            }

            if (string.IsNullOrEmpty(detail)) detail = Truncate(response.Body);
            string hint = response.Code == 401 || response.Code == 403 ? "\nCheck that the key is a service_role/secret key for this project." : "";
            string raw = response.Body ?? "";
            if (raw.Contains("icon_style") || raw.Contains("icon_background") || raw.Contains("icon_inset"))
                hint += "\nThe database needs the 20260921000000_games_icon_style.sql migration (supabase db push).";
            return "Request failed (" + (response.Code > 0 ? "HTTP " + response.Code : response.Error) + "): " + detail + hint;
        }

        private static string Truncate(string text) => text != null && text.Length > 500 ? text.Substring(0, 500) + "..." : text;

        private void Fail(string message)
        {
            _isBusy = false;
            SetStatus(message, MessageType.Error);
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            if (this != null) Repaint();
        }

        // =====================================================================================================
        // Paths
        // =====================================================================================================

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        private static string ToFullPath(string path) =>
            Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));

        private static string ToProjectRelativeIfPossible(string absolutePath)
        {
            string full = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        // =====================================================================================================
        // Look and feel
        // =====================================================================================================

        private static class Palette
        {
            private static bool Dark => EditorGUIUtility.isProSkin;

            public static readonly Color Accent = new Color(0.20f, 0.55f, 0.95f);
            public static readonly Color New = new Color(0.25f, 0.60f, 0.95f);
            public static readonly Color Modified = new Color(0.93f, 0.62f, 0.13f);
            public static readonly Color Synced = new Color(0.22f, 0.70f, 0.42f);
            public static readonly Color Hidden = new Color(0.62f, 0.42f, 0.86f);
            public static readonly Color Retired = new Color(0.50f, 0.52f, 0.56f);
            public static readonly Color Danger = new Color(0.86f, 0.26f, 0.26f);

            public static Color Header => Dark ? new Color(0.13f, 0.15f, 0.19f) : new Color(0.82f, 0.86f, 0.93f);
            public static Color Tile => Dark ? new Color(0.24f, 0.25f, 0.28f) : new Color(0.89f, 0.90f, 0.92f);
            public static Color TileSelected => Dark ? new Color(0.30f, 0.33f, 0.40f) : new Color(0.80f, 0.86f, 0.96f);
        }

        private sealed class Styles
        {
            public readonly GUIStyle Card;
            public readonly GUIStyle HeaderTitle;
            public readonly GUIStyle HeaderSub;
            public readonly GUIStyle TileNumber;
            public readonly GUIStyle TileLabel;
            public readonly GUIStyle CardTitle;
            public readonly GUIStyle Mini;
            public readonly GUIStyle Hint;
            public readonly GUIStyle MiniDanger;
            public readonly GUIStyle Pill;
            public readonly GUIStyle Section;
            public readonly GUIStyle WrapArea;
            public readonly GUIStyle AddButton;
            public readonly GUIStyle EmptyTitle;
            public readonly GUIStyle EmptySub;
            public readonly GUIStyle IconPlaceholder;

            public Styles()
            {
                Card = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(14, 10, 6, 8),
                    margin = new RectOffset(8, 8, 3, 3),
                };
                HeaderTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 17, alignment = TextAnchor.MiddleLeft };
                HeaderSub = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
                TileNumber = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
                TileLabel = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                CardTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                Mini = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                Hint = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 0.85f) }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                MiniDanger = new GUIStyle(Mini) { normal = { textColor = Palette.Danger } };
                Pill = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                Section = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 10 };
                WrapArea = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
                AddButton = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
                EmptyTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
                EmptySub = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                IconPlaceholder = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            }
        }
    }
}
