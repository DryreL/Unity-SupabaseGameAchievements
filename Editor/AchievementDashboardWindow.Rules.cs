using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// The Rules tab: decide when each achievement unlocks and hook it to a button, a UnityEvent or a bool
    /// member of your own scripts in any open scene, without writing code. See <see cref="AchievementRules"/>.
    /// </summary>
    public sealed partial class AchievementDashboardWindow
    {
        private enum Tab { Achievements, Rules, Overlay, Debug }

        /// <summary>The "add hookups" form of one role of one rule, kept between repaints.</summary>
        private sealed class BindingDraft
        {
            /// <summary>The scene objects to hook up. The component and member below are chosen from the first one.</summary>
            public readonly List<GameObject> Targets = new List<GameObject>();
            public int ComponentIndex;
            public int SourceIndex;
            public int MemberIndex;
            public int Amount = 1;
            public bool PicksInitialized;

            public void ResetPicks()
            {
                ComponentIndex = 0;
                MemberIndex = 0;
                PicksInitialized = false;
            }
        }

        private static readonly string[] KindLabels =
        {
            "Unlock when triggered",
            "Counter  (unlock after N triggers)",
            "Flawless run  (start / fail / complete)",
        };

        private Tab _tab;
        private Vector2 _rulesScroll;
        private bool _showRulesSettings;
        private Dictionary<string, UnityEngine.Object> _bindingScan = new Dictionary<string, UnityEngine.Object>();
        private double _nextBindingScan;
        private readonly Dictionary<string, BindingDraft> _drafts = new Dictionary<string, BindingDraft>();
        private bool _rulesFileStateDirty = true;
        private bool _rulesFileMissing;
        private bool _rulesFileStale;

        private void OnHierarchyChange()
        {
            _nextBindingScan = 0;
            _setup = null; // a bootstrap added to (or removed from) an open scene counts right away
        }

        private void RefreshBindingScan()
        {
            _bindingScan = AchievementRuleWiring.Scan(_data.Rules.SelectMany(r => r.Bindings).Select(b => b.ScenePath));
            _nextBindingScan = EditorApplication.timeSinceStartup + 1.0;
        }

        private void RefreshRulesFileState()
        {
            _rulesFileStateDirty = false;
            string generated = _data.BuildRuleSet(out _).ToJson();
            string path = ToFullPath(_data.RulesPath);
            _rulesFileMissing = !File.Exists(path);

            if (_rulesFileMissing) _rulesFileStale = _data.Rules.Count > 0;
            else
            {
                try { _rulesFileStale = File.ReadAllText(path).Replace("\r\n", "\n") != generated.Replace("\r\n", "\n"); }
                catch (IOException) { _rulesFileStale = true; }
            }
        }

        // =====================================================================================================
        // Tab strip
        // =====================================================================================================

        private void DrawTabs()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                int selected = GUILayout.Toolbar((int)_tab, new[] { "Achievements", "Rules  (" + _data.Rules.Count + ")", "Overlay", "Debug" }, GUILayout.Height(24));
                GUILayout.Space(8);
                if (selected != (int)_tab)
                {
                    _tab = (Tab)selected;
                    GUI.FocusControl(null);
                    if (_tab == Tab.Rules) RefreshBindingScan();
                }
            }
        }

        // =====================================================================================================
        // Rules tab
        // =====================================================================================================

        private void DrawRulesTab()
        {
            if (_rulesFileStateDirty) RefreshRulesFileState();

            DrawRulesToolbar();
            DrawGameSetup();

            if (_loadError != null) EditorGUILayout.HelpBox(_loadError + "\nNothing is saved until this is resolved.", MessageType.Error);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);

            _showRulesSettings = EditorGUILayout.Foldout(_showRulesSettings, "Rules file", true);
            if (_showRulesSettings)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    _data.RulesPath = EditorGUILayout.TextField(
                        new GUIContent("Rules Path", "Where Write rules.json saves the rules. It must be inside a Resources folder; the default is found automatically by UnityAchievementManager (Rules Resource: Achievements/rules)."),
                        _data.RulesPath);
                    if (EditorGUI.EndChangeCheck()) MarkDirty();
                    EditorGUILayout.LabelField("The game loads this file by itself; there is nothing to add to a scene.", _styles.Mini);
                }
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(_rulesScroll))
            {
                _rulesScroll = scroll.scrollPosition;

                if (_data.Rules.Count == 0)
                {
                    EditorGUILayout.Space(20);
                    GUILayout.Label("No rules yet.", _styles.EmptyTitle);
                    GUILayout.Label("A rule says when an achievement unlocks: when a button is clicked, after a counter reaches a number, or when a task finishes without failing. Press + Add Rule.", _styles.EmptySub);
                }

                DashboardRule toRemove = null;
                foreach (var rule in _data.Rules.ToList())
                    if (DrawRuleCard(rule)) toRemove = rule;

                if (toRemove != null) RemoveRule(toRemove);

                EditorGUILayout.Space(6);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    var previous = GUI.backgroundColor;
                    GUI.backgroundColor = Palette.Accent;
                    if (GUILayout.Button(new GUIContent("+   Add Rule", "Add a rule for an achievement."), _styles.AddButton, GUILayout.Height(34)))
                    {
                        AddRule();
                        GUIUtility.ExitGUI();
                    }
                    GUI.backgroundColor = previous;
                    GUILayout.Space(8);
                }
                EditorGUILayout.Space(12);
            }
        }

        private void DrawRulesToolbar()
        {
            EditorGUILayout.Space(2);
            bool narrow = NarrowWindow;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(new GUIContent("+ Add Rule", "Add a rule for an achievement."), EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    AddRule();
                    GUIUtility.ExitGUI();
                }

                GUILayout.FlexibleSpace();
                if (!narrow) DrawRulesFileState();
            }

            if (narrow)
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                    DrawRulesFileState();
        }

        private void DrawRulesFileState()
        {
            var previousColor = GUI.contentColor;
            if (_rulesFileStale) GUI.contentColor = Palette.Modified;
            GUILayout.Label(_rulesFileStale ? (_rulesFileMissing ? "rules.json not written yet" : "rules.json is out of date") : "rules.json is up to date", EditorStyles.miniLabel);
            GUI.contentColor = previousColor;

            GUILayout.Space(8);
            if (GUILayout.Button(new GUIContent("Refresh", "Look for the triggers in the open scenes again."), EditorStyles.toolbarButton, GUILayout.Width(60)))
                RefreshBindingScan();
            if (GUILayout.Button(new GUIContent("Write rules.json", "Save the rules where the game loads them from."), EditorStyles.toolbarButton, GUILayout.Width(108)))
                WriteRulesFile();
        }

        private void AddRule()
        {
            _data.AddRule();
            MarkDirty();
            _rulesScroll.y = float.MaxValue;
            GUI.FocusControl(null);
        }

        // =====================================================================================================
        // One rule
        // =====================================================================================================

        /// <returns>True when the delete button was pressed.</returns>
        private bool DrawRuleCard(DashboardRule rule)
        {
            var errors = _data.ValidateRule(rule);
            var achievement = _data.Achievements.FirstOrDefault(a => a.Key == rule.AchievementKey);
            Color accent = errors.Count > 0 ? Palette.Danger : rule.Bindings.Count == 0 ? Palette.Modified : Palette.Synced;
            bool remove = false;

            Rect card = EditorGUILayout.BeginVertical(_styles.Card);
            Rect header = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            float right = header.xMax;

            // Buttons first: the first control to claim a click gets it (same as the achievement cards).
            right -= 28;
            if (GUI.Button(new Rect(right, header.y + 8, 24, 22), new GUIContent("x", "Delete this rule and remove its triggers from the open scenes"), EditorStyles.miniButton)) remove = true;
            right -= 8;

            right -= 96;
            DrawPill(new Rect(right, header.y + 10, 96, 18), RuleKindName(rule).ToUpperInvariant(), Palette.Accent);
            right -= 6;

            rule.Expanded = EditorGUI.Foldout(new Rect(header.x, header.y + 11, 14, 16), rule.Expanded, GUIContent.none, false);
            if (GUI.Button(new Rect(header.x + 14, header.y, Mathf.Max(0, right - header.x - 14), header.height), GUIContent.none, GUIStyle.none))
            {
                rule.Expanded = !rule.Expanded;
                GUI.FocusControl(null);
            }

            if (Event.current.type == EventType.Repaint)
            {
                float textWidth = Mathf.Max(0, right - header.x - 26);
                string title = achievement != null && !string.IsNullOrEmpty(achievement.Title) ? achievement.Title
                    : string.IsNullOrEmpty(rule.AchievementKey) ? "(choose an achievement)" : rule.AchievementKey;
                GUI.Label(new Rect(header.x + 22, header.y + 2, textWidth, 20), title, _styles.CardTitle);

                string sub = rule.Bindings.Count + " hookup(s)";
                if (rule.Kind == AchievementRuleKind.Counter) sub = "x" + rule.Target + "   |   " + sub;
                if (errors.Count > 0) sub += "   |   needs attention";
                else if (rule.Bindings.Count == 0) sub += "   |   nothing triggers this yet";
                GUI.Label(new Rect(header.x + 22, header.y + 20, textWidth, 16), sub, errors.Count > 0 ? _styles.MiniDanger : _styles.Mini);
            }

            if (rule.Expanded) DrawRuleBody(rule, errors);

            EditorGUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(card.x + 1, card.y + 1, 4, card.height - 2), accent);

            return remove;
        }

        private static string RuleKindName(DashboardRule rule)
        {
            switch (rule.Kind)
            {
                case AchievementRuleKind.Counter: return "Counter";
                case AchievementRuleKind.Run: return "Flawless run";
                default: return "Unlock";
            }
        }

        private void DrawRuleBody(DashboardRule rule, List<string> errors)
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 118;
            EditorGUILayout.Space(2);

            EditorGUI.BeginChangeCheck();
            DrawAchievementPopup(rule);

            int kindIndex = EditorGUILayout.Popup(new GUIContent("Rule", "How the achievement unlocks."), (int)rule.Kind, KindLabels);
            if (kindIndex != (int)rule.Kind)
            {
                var kind = (AchievementRuleKind)kindIndex;
                if (rule.CanChangeKindTo(kind)) rule.Kind = kind;
                else SetStatus("A flawless run has three triggers (start, fail, complete). Unbind this rule's hookups before switching to or from it.", MessageType.Warning);
            }

            if (rule.Kind == AchievementRuleKind.Counter)
                rule.Target = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Target", "How many triggers (counting each one's amount) it takes."), rule.Target));
            if (EditorGUI.EndChangeCheck()) MarkDirty();

            DrawEventNames(rule);

            foreach (string role in rule.Roles)
                DrawRoleSection(rule, role);

            EditorGUIUtility.labelWidth = previousLabelWidth;

            if (errors.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(string.Join("\n", errors), MessageType.Error);
            }
        }

        private void DrawAchievementPopup(DashboardRule rule)
        {
            var options = _data.Achievements
                .Where(a => !a.Retired || a.Key == rule.AchievementKey)
                .OrderBy(a => a.BitIndex)
                .ToList();
            var labels = new List<string> { "(choose an achievement)" };
            labels.AddRange(options.Select(a => a.Key + "  -  " + a.Title));

            int index = options.FindIndex(a => a.Key == rule.AchievementKey) + 1;
            if (index == 0 && !string.IsNullOrEmpty(rule.AchievementKey))
            {
                labels.Insert(1, rule.AchievementKey + "  (not in the dashboard)");
                index = 1;
            }

            int picked = EditorGUILayout.Popup(new GUIContent("Achievement", "The achievement this rule unlocks."), index, labels.ToArray());
            if (picked == index) return;

            bool hasMissingEntry = labels.Count > options.Count + 1;
            int optionIndex = picked - 1 - (hasMissingEntry ? 1 : 0);
            if (picked == 0) rule.AchievementKey = "";
            else if (optionIndex >= 0 && optionIndex < options.Count) rule.AchievementKey = options[optionIndex].Key;
        }

        private void DrawEventNames(DashboardRule rule)
        {
            foreach (string role in rule.Roles)
            {
                string name = rule.EventName(role);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent(rule.Roles.Count > 1 ? "Event (" + role + ")" : "Event", "What a hookup reports. From your own code you can also call AchievementEvents.Report(\"" + name + "\")."));
                    EditorGUILayout.SelectableLabel(name, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }
            }
        }

        private void DrawRoleSection(DashboardRule rule, string role)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(RoleTitle(rule, role), _styles.Section);

            foreach (var binding in rule.Bindings.Where(b => b.Role == role).ToList())
                DrawBindingRow(rule, binding);

            DrawDraft(rule, role);
        }

        private static string RoleTitle(DashboardRule rule, string role)
        {
            switch (role)
            {
                case "start": return "Start  -  begins an attempt";
                case "fail": return "Fail  -  spoils the attempt";
                case "complete": return "Complete  -  unlocks if the attempt was not spoiled";
                default: return rule.Kind == AchievementRuleKind.Counter ? "Triggers  -  each one counts" : "Triggers";
            }
        }

        // =====================================================================================================
        // Existing hookups
        // =====================================================================================================

        private void DrawBindingRow(DashboardRule rule, DashboardBinding binding)
        {
            _bindingScan.TryGetValue(binding.Id, out var live);
            bool isPrefab = AchievementRuleWiring.IsPrefabPath(binding.ScenePath);
            bool available = AchievementRuleWiring.IsContainerAvailable(binding.ScenePath);

            // Where the trigger is, as far as can be told without opening anything.
            bool verifiable = false;
            bool inClosedFile = live == null && !isPrefab && !available &&
                AchievementRuleWiring.ClosedFileContains(binding.ScenePath, binding.Id, out verifiable);

            string status;
            Color color;
            if (live != null) { status = isPrefab ? "IN PREFAB" : "IN SCENE"; color = Palette.Synced; }
            else if (available) { status = "MISSING"; color = Palette.Danger; }
            else if (inClosedFile) { status = "IN CLOSED SCENE"; color = Palette.New; }
            else if (verifiable) { status = "MISSING"; color = Palette.Danger; }
            else { status = isPrefab ? "PREFAB NOT FOUND" : "SCENE CLOSED"; color = Palette.Retired; }

            bool canOpen = live == null || status == "IN CLOSED SCENE";
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Rect pill = GUILayoutUtility.GetRect(112, 18, GUILayout.Width(112), GUILayout.Height(18));
                DrawPill(pill, status, color);

                string sceneName = Path.GetFileNameWithoutExtension(binding.ScenePath);
                string kind = binding.Source == DashboardBindingSource.UnityEvent ? "event" : "condition";
                string text = sceneName + "  /  " + binding.Describe() + "  (" + kind + (rule.Kind == AchievementRuleKind.Counter ? ", x" + binding.Amount : "") + ")";
                GUILayout.Label(new GUIContent(text, binding.ScenePath + "\n" + text), _styles.Mini, GUILayout.ExpandWidth(true));

                if (live != null)
                {
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(52)))
                    {
                        Selection.activeObject = live;
                        EditorGUIUtility.PingObject(live);
                    }
                }
                else if (canOpen && (isPrefab ? available : true))
                {
                    if (GUILayout.Button(isPrefab ? "Open prefab" : "Open scene", EditorStyles.miniButton, GUILayout.Width(isPrefab ? 78 : 74)))
                        OpenContainer(binding.ScenePath);
                }

                if (GUILayout.Button(new GUIContent("Unbind", "Remove the trigger and this hookup from the rule."), EditorStyles.miniButton, GUILayout.Width(56)))
                    Unbind(rule, binding);
            }

            if (status == "MISSING")
                EditorGUILayout.LabelField("The trigger is gone from " + (isPrefab ? "the prefab" : "the scene") + " (deleted by hand?). Unbind and add the hookup again.", _styles.MiniDanger);
        }

        private void OpenContainer(string path)
        {
            if (AchievementRuleWiring.IsPrefabPath(path))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset != null) AssetDatabase.OpenAsset(asset);
            }
            else
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EditorSceneManager.OpenScene(path);
            }
            RefreshBindingScan();
            GUIUtility.ExitGUI();
        }

        private void Unbind(DashboardRule rule, DashboardBinding binding)
        {
            bool removed = AchievementRuleWiring.Unbind(binding);
            bool available = AchievementRuleWiring.IsContainerAvailable(binding.ScenePath);

            if (!removed && !available && !EditorUtility.DisplayDialog("Not open",
                    "'" + binding.ScenePath + "' is not open, so its trigger cannot be removed now. Removing the hookup here leaves that trigger where it is; " +
                    "it only reports an event nothing listens to any more and can be deleted by hand.\n\nRemove the hookup anyway?", "Remove hookup", "Cancel"))
                return;

            rule.Bindings.Remove(binding);
            MarkDirty();
            RefreshBindingScan();
            string saveHint = AchievementRuleWiring.IsPrefabPath(binding.ScenePath) ? "The prefab was saved." : "Save the scene (Ctrl+S) to keep the change.";
            SetStatus(removed ? "Unbound. " + saveHint : "Removed the hookup from the rule.", MessageType.Info);
            GUIUtility.ExitGUI();
        }

        private void RemoveRule(DashboardRule rule)
        {
            int reachable = rule.Bindings.Count(b => _bindingScan.ContainsKey(b.Id));
            int elsewhere = rule.Bindings.Count - reachable;
            string message = "Delete this rule" + (rule.Bindings.Count > 0 ? " and remove " + reachable + " trigger(s) from the open scenes and prefabs" : "") + "?";
            if (elsewhere > 0) message += "\n" + elsewhere + " hookup(s) are in scenes that are not open; their triggers stay there and do nothing.";
            if (!EditorUtility.DisplayDialog("Delete rule", message, "Delete", "Cancel")) return;

            foreach (var binding in rule.Bindings.ToList()) AchievementRuleWiring.Unbind(binding);
            _data.Rules.Remove(rule);
            MarkDirty();
            RefreshBindingScan();
            SetStatus("Deleted the rule. Save the scene(s) and write rules.json to finish.", MessageType.Info);
            GUIUtility.ExitGUI();
        }

        // =====================================================================================================
        // Adding a hookup
        // =====================================================================================================

        private void DrawDraft(DashboardRule rule, string role)
        {
            string draftKey = rule.Id + ":" + role;
            if (!_drafts.TryGetValue(draftKey, out var draft)) _drafts[draftKey] = draft = new BindingDraft();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Add hookups  (as many objects as you like, they all trigger this rule)", _styles.Mini);

                // The objects picked so far.
                draft.Targets.RemoveAll(t => t == null);
                GameObject toRemove = null;
                foreach (var target in draft.Targets)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.ObjectField(target, typeof(GameObject), true);
                        if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(22))) toRemove = target;
                    }
                }
                if (toRemove != null)
                {
                    draft.Targets.Remove(toRemove);
                    draft.ResetPicks();
                }

                // Adding more: the object field, the current selection, or a drop from the Hierarchy.
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (GameObject)EditorGUILayout.ObjectField(
                        new GUIContent("Add object", "Pick a GameObject (a Button, say) from the Hierarchy of an open scene."), null, typeof(GameObject), true);
                    if (picked != null) AddDraftTarget(draft, picked);

                    if (GUILayout.Button(new GUIContent("Use selection", "Add every scene object currently selected in the Hierarchy."), GUILayout.Width(96)))
                        foreach (var selected in Selection.gameObjects) AddDraftTarget(draft, selected);
                }
                DrawDropArea(draft);

                if (draft.Targets.Count == 0) return;

                // What to hook into comes first: it decides which of the object's components are worth offering.
                int previousSource = draft.SourceIndex;
                draft.SourceIndex = EditorGUILayout.Popup(
                    new GUIContent("Hook into", "A UnityEvent calls the rule when it is invoked (Button click, Toggle, or a UnityEvent field of your script). A condition is a bool member of your script that the rule watches."),
                    draft.SourceIndex, new[] { "UnityEvent  (button click, toggle, your own event)", "Condition  (a bool method / property / field)" });
                if (draft.SourceIndex != previousSource) draft.ResetPicks();
                bool wantEvents = draft.SourceIndex == 0;

                // Component and member come from the first object; the others use the same type and member.
                // Components that offer something are listed first: an object's own first component is always its
                // Transform, which never does, so defaulting to "the first component" would find nothing to hook.
                var first = draft.Targets[0];
                var components = AchievementRuleWiring.HookableComponents(first)
                    .Select(c => new HookableComponent(c, HookableCount(c, wantEvents)))
                    .OrderByDescending(x => x.Count > 0 ? 1 : 0)
                    .ToList();

                if (components.Count == 0 || components[0].Count == 0)
                {
                    DrawNothingToHook(draft, first, wantEvents);
                    return;
                }

                // Default pick: a Button's onClick if the object has one, since that is what most hookups are.
                // Otherwise fall back to the first component that has something to hook (already first in the list).
                if (!draft.PicksInitialized)
                {
                    draft.PicksInitialized = true;
                    if (wantEvents)
                    {
                        for (int i = 0; i < components.Count; i++)
                        {
                            if (!(components[i].Component is UnityEngine.UI.Button)) continue;
                            var events = AchievementRuleWiring.FindEvents(components[i].Component);
                            int onClickIndex = events.FindIndex(e => e.Name == "onClick");
                            if (onClickIndex < 0) continue;
                            draft.ComponentIndex = i;
                            draft.MemberIndex = onClickIndex;
                            break;
                        }
                    }
                }

                draft.ComponentIndex = Mathf.Clamp(draft.ComponentIndex, 0, components.Count - 1);
                draft.ComponentIndex = EditorGUILayout.Popup(
                    new GUIContent("Component", draft.Targets.Count > 1 ? "Chosen from the first object; every other object uses its component of the same type." : "The component to hook into. Components with something to hook are listed first."),
                    draft.ComponentIndex, ComponentLabels(components, wantEvents));
                var component = components[draft.ComponentIndex].Component;

                string[] members = wantEvents
                    ? AchievementRuleWiring.FindEvents(component).Select(e => e.Name).ToArray()
                    : AchievementRuleWiring.FindConditions(component).Select(m => m.ToString()).ToArray();
                string[] memberNames = wantEvents
                    ? members
                    : AchievementRuleWiring.FindConditions(component).Select(m => m.Name).ToArray();

                if (members.Length == 0)
                {
                    EditorGUILayout.HelpBox("This component has nothing to hook into. Pick one of the components marked with a count.", MessageType.None);
                    return;
                }

                draft.MemberIndex = Mathf.Clamp(draft.MemberIndex, 0, members.Length - 1);
                draft.MemberIndex = EditorGUILayout.Popup("Member", draft.MemberIndex, members);

                if (rule.Kind == AchievementRuleKind.Counter && role == "trigger")
                    draft.Amount = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Counts as", "How much each trigger adds to the counter."), draft.Amount));

                if (draft.SourceIndex == 1)
                    EditorGUILayout.LabelField("Fires each time the value turns true (checked 5 times a second). With IL2CPP code stripping, mark the member [UnityEngine.Scripting.Preserve].", _styles.Mini);

                string bindLabel = draft.Targets.Count > 1 ? "Bind " + draft.Targets.Count + " objects" : "Bind";
                if (GUILayout.Button(bindLabel, GUILayout.Height(24)))
                    BindDraft(rule, role, draft, component.GetType(), memberNames[draft.MemberIndex]);
            }
        }

        private void AddDraftTarget(BindingDraft draft, GameObject go)
        {
            if (go == null || draft.Targets.Contains(go)) return;
            if (EditorUtility.IsPersistent(go))
            {
                SetStatus("'" + go.name + "' is a prefab asset. Pick the object from a scene (open the prefab or the scene that contains it).", MessageType.Warning);
                return;
            }
            draft.Targets.Add(go);
            if (draft.Targets.Count == 1) draft.ResetPicks();
        }

        private void DrawDropArea(BindingDraft draft)
        {
            Rect area = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
            GUI.Box(area, "or drop scene objects here (from the Hierarchy)", EditorStyles.helpBox);

            var current = Event.current;
            if ((current.type != EventType.DragUpdated && current.type != EventType.DragPerform) || !area.Contains(current.mousePosition)) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var dropped in DragAndDrop.objectReferences)
                    AddDraftTarget(draft, dropped as GameObject ?? (dropped as Component)?.gameObject);
            }
            current.Use();
        }

        private readonly struct HookableComponent
        {
            public HookableComponent(Component component, int count)
            {
                Component = component;
                Count = count;
            }

            public Component Component { get; }

            /// <summary>How many UnityEvents (or bool conditions) it offers.</summary>
            public int Count { get; }
        }

        private static int HookableCount(Component component, bool wantEvents) =>
            wantEvents ? AchievementRuleWiring.FindEvents(component).Count : AchievementRuleWiring.FindConditions(component).Count;

        /// <summary>Nothing on the picked object can be hooked into: say why, and offer a child that can.</summary>
        private void DrawNothingToHook(BindingDraft draft, GameObject picked, bool wantEvents)
        {
            EditorGUILayout.HelpBox(wantEvents
                ? "Nothing on '" + picked.name + "' exposes a UnityEvent (a Button's onClick, a Toggle, or an event field of one of your scripts)."
                : "Nothing on '" + picked.name + "' has a bool method, property or field to watch.", MessageType.None);

            if (wantEvents && picked.GetComponent<AchievementColliderEvents>() == null &&
                (picked.GetComponent<Collider>() != null || picked.GetComponent<Collider2D>() != null))
            {
                EditorGUILayout.Space(2);
                if (GUILayout.Button("Add 'Achievement Collider Events'  (so an OnTrigger/OnCollision can unlock this)"))
                {
                    Undo.AddComponent<AchievementColliderEvents>(picked);
                    draft.ResetPicks();
                }
                return;
            }

            var child = picked.GetComponentsInChildren<Transform>(true)
                .Select(t => t.gameObject)
                .Skip(1) // the first entry is the picked object itself
                .FirstOrDefault(g => AchievementRuleWiring.HookableComponents(g).Any(c => HookableCount(c, wantEvents) > 0));

            if (child != null && GUILayout.Button("Use the child '" + AchievementRuleWiring.ObjectPath(child) + "' instead"))
            {
                draft.Targets[0] = child;
                draft.ResetPicks();
            }
        }

        private static string[] ComponentLabels(List<HookableComponent> components, bool wantEvents)
        {
            var seen = new Dictionary<string, int>();
            return components.Select(x =>
            {
                string name = x.Component.GetType().Name;
                seen[name] = seen.TryGetValue(name, out int count) ? count + 1 : 1;
                if (seen[name] > 1) name += " #" + seen[name];

                string what = wantEvents ? "event" : "condition";
                return x.Count > 0 ? name + "  (" + x.Count + " " + what + (x.Count > 1 ? "s" : "") + ")" : name + "  (nothing to hook)";
            }).ToArray();
        }

        private void BindDraft(DashboardRule rule, string role, BindingDraft draft, Type componentType, string member)
        {
            bool isEvent = draft.SourceIndex == 0;
            string eventName = rule.EventName(role);
            int bound = 0;
            var problems = new List<string>();
            var done = new List<GameObject>();

            foreach (var target in draft.Targets.ToList())
            {
                var component = AchievementRuleWiring.HookableComponents(target).FirstOrDefault(c => c.GetType() == componentType);
                if (component == null) { problems.Add(target.name + ": no " + componentType.Name); continue; }

                var binding = new DashboardBinding
                {
                    Role = role,
                    Source = isEvent ? DashboardBindingSource.UnityEvent : DashboardBindingSource.Condition,
                    ScenePath = target.scene.path,
                    ObjectPath = AchievementRuleWiring.ObjectPath(target),
                    ComponentType = componentType.Name,
                    Member = member,
                    Amount = Mathf.Max(1, draft.Amount),
                };

                if (rule.Bindings.Any(b => b.SameHookup(binding))) { problems.Add(target.name + ": already hooked up"); continue; }

                string error = isEvent
                    ? AchievementRuleWiring.BindEvent(component, member, eventName, binding)
                    : AchievementRuleWiring.BindCondition(component, member, eventName, binding);
                if (error != null) { problems.Add(target.name + ": " + error); continue; }

                rule.Bindings.Add(binding);
                done.Add(target);
                bound++;
            }

            // Objects that failed stay in the list so they can be fixed or removed; the rest are done.
            foreach (var finished in done) draft.Targets.Remove(finished);
            if (draft.Targets.Count == 0) draft.ResetPicks();

            if (bound > 0)
            {
                MarkDirty();
                RefreshBindingScan();
            }

            string message = bound > 0
                ? "Hooked " + bound + " object(s) to '" + eventName + "'. Save the scene (Ctrl+S) to keep the triggers, and write rules.json."
                : "Nothing was hooked up.";
            if (problems.Count > 0) message += " Skipped: " + string.Join("; ", problems) + ".";
            SetStatus(message, problems.Count > 0 ? MessageType.Warning : MessageType.Info);
            GUIUtility.ExitGUI();
        }

        // =====================================================================================================
        // rules.json
        // =====================================================================================================

        private void WriteRulesFile()
        {
            var set = _data.BuildRuleSet(out var skipped);
            string fullPath = ToFullPath(_data.RulesPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, set.ToJson(), new UTF8Encoding(false));

                string relative = ToProjectRelativeIfPossible(fullPath);
                if (relative.StartsWith("Assets/", StringComparison.Ordinal)) AssetDatabase.ImportAsset(relative);
            }
            catch (Exception e)
            {
                SetStatus("Could not write rules.json: " + e.Message, MessageType.Error);
                return;
            }

            _rulesFileStateDirty = true;
            string message = "Wrote " + set.Rules.Count + " rule(s) to " + _data.RulesPath + ".";
            if (skipped.Count > 0) message += " Skipped (fix them first): " + string.Join("; ", skipped) + ".";
            if (!_data.RulesPath.Replace('\\', '/').Contains("/Resources/")) message += " This path is not in a Resources folder, so the game will not find it by itself.";
            SetStatus(message, skipped.Count > 0 ? MessageType.Warning : MessageType.Info);
        }
    }
}
