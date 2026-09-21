using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private enum Tab { Achievements, Rules }

        /// <summary>The "add a hookup" form of one role of one rule, kept between repaints.</summary>
        private sealed class BindingDraft
        {
            public GameObject Target;
            public int ComponentIndex;
            public int SourceIndex;
            public int MemberIndex;
            public int Amount = 1;

            public void ResetPicks()
            {
                ComponentIndex = 0;
                MemberIndex = 0;
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

        private void OnHierarchyChange() => _nextBindingScan = 0;

        private void RefreshBindingScan()
        {
            _bindingScan = AchievementRuleWiring.Scan();
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
                try { _rulesFileStale = File.ReadAllText(path).Replace("\r\n", "\n") != generated; }
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
                int selected = GUILayout.Toolbar((int)_tab, new[] { "Achievements", "Rules  (" + _data.Rules.Count + ")" }, GUILayout.Height(24));
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
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(new GUIContent("+ Add Rule", "Add a rule for an achievement."), EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    AddRule();
                    GUIUtility.ExitGUI();
                }

                GUILayout.FlexibleSpace();

                var previousColor = GUI.contentColor;
                if (_rulesFileStale) GUI.contentColor = Palette.Modified;
                GUILayout.Label(_rulesFileStale ? (_rulesFileMissing ? "rules.json not written yet" : "rules.json is out of date") : "rules.json is up to date", EditorStyles.miniLabel);
                GUI.contentColor = previousColor;

                if (GUILayout.Button(new GUIContent("Refresh", "Look for the triggers in the open scenes again."), EditorStyles.toolbarButton, GUILayout.Width(60)))
                    RefreshBindingScan();
                if (GUILayout.Button(new GUIContent("Write rules.json", "Save the rules where the game loads them from."), EditorStyles.toolbarButton, GUILayout.Width(108)))
                    WriteRulesFile();
            }
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
            bool sceneOpen = AchievementRuleWiring.IsSceneLoaded(binding.ScenePath);
            string status = live != null ? "IN SCENE" : sceneOpen ? "MISSING" : "SCENE CLOSED";
            Color color = live != null ? Palette.Synced : sceneOpen ? Palette.Danger : Palette.Retired;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Rect pill = GUILayoutUtility.GetRect(84, 18, GUILayout.Width(84), GUILayout.Height(18));
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
                else if (!sceneOpen && GUILayout.Button("Open scene", EditorStyles.miniButton, GUILayout.Width(74)))
                {
                    OpenScene(binding.ScenePath);
                }

                if (GUILayout.Button(new GUIContent("Unbind", "Remove the trigger from the scene and this hookup from the rule."), EditorStyles.miniButton, GUILayout.Width(56)))
                    Unbind(rule, binding);
            }

            if (live == null && sceneOpen)
                EditorGUILayout.LabelField("The trigger is gone from the scene (deleted by hand?). Unbind and add the hookup again.", _styles.MiniDanger);
        }

        private void OpenScene(string scenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(scenePath);
            RefreshBindingScan();
            GUIUtility.ExitGUI();
        }

        private void Unbind(DashboardRule rule, DashboardBinding binding)
        {
            bool removedFromScene = AchievementRuleWiring.Unbind(binding.Id);
            bool sceneOpen = AchievementRuleWiring.IsSceneLoaded(binding.ScenePath);

            if (!removedFromScene && !sceneOpen && !EditorUtility.DisplayDialog("Scene is not open",
                    "The scene '" + binding.ScenePath + "' is not open, so its trigger cannot be removed now. Removing the hookup here leaves that trigger in the scene; " +
                    "it only reports an event nothing listens to any more and can be deleted by hand.\n\nRemove the hookup anyway?", "Remove hookup", "Cancel"))
                return;

            rule.Bindings.Remove(binding);
            MarkDirty();
            RefreshBindingScan();
            SetStatus(removedFromScene
                ? "Unbound. Save the scene (Ctrl+S) to keep the change."
                : "Removed the hookup from the rule.", MessageType.Info);
            GUIUtility.ExitGUI();
        }

        private void RemoveRule(DashboardRule rule)
        {
            int inScenes = rule.Bindings.Count(b => _bindingScan.ContainsKey(b.Id));
            int elsewhere = rule.Bindings.Count - inScenes;
            string message = "Delete this rule" + (rule.Bindings.Count > 0 ? " and remove " + inScenes + " trigger(s) from the open scenes" : "") + "?";
            if (elsewhere > 0) message += "\n" + elsewhere + " hookup(s) are in scenes that are not open; their triggers stay there and do nothing.";
            if (!EditorUtility.DisplayDialog("Delete rule", message, "Delete", "Cancel")) return;

            foreach (var binding in rule.Bindings.ToList()) AchievementRuleWiring.Unbind(binding.Id);
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
                EditorGUILayout.LabelField("Add a hookup", _styles.Mini);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = (GameObject)EditorGUILayout.ObjectField(
                        new GUIContent("Scene object", "Drag a GameObject (a Button, say) from the Hierarchy of an open scene."),
                        draft.Target, typeof(GameObject), true);
                    if (picked != draft.Target)
                    {
                        draft.Target = picked;
                        draft.ResetPicks();
                    }

                    if (GUILayout.Button(new GUIContent("Use selection", "Take the object currently selected in the Hierarchy."), GUILayout.Width(96)))
                    {
                        draft.Target = Selection.activeGameObject;
                        draft.ResetPicks();
                    }
                }

                if (draft.Target == null) return;

                var components = AchievementRuleWiring.HookableComponents(draft.Target);
                if (components.Count == 0) { EditorGUILayout.HelpBox("This object has nothing to hook into.", MessageType.None); return; }

                draft.ComponentIndex = Mathf.Clamp(draft.ComponentIndex, 0, components.Count - 1);
                draft.ComponentIndex = EditorGUILayout.Popup("Component", draft.ComponentIndex, ComponentLabels(components));
                var component = components[draft.ComponentIndex];

                draft.SourceIndex = EditorGUILayout.Popup(
                    new GUIContent("Hook into", "A UnityEvent calls the rule when it is invoked (Button click, Toggle, or a UnityEvent field of your script). A condition is a bool member of your script that the rule watches."),
                    draft.SourceIndex, new[] { "UnityEvent  (button click, toggle, your own event)", "Condition  (a bool method / property / field)" });

                string[] members = draft.SourceIndex == 0
                    ? AchievementRuleWiring.FindEvents(component).Select(e => e.Name).ToArray()
                    : AchievementRuleWiring.FindConditions(component).Select(m => m.ToString()).ToArray();
                string[] memberNames = draft.SourceIndex == 0
                    ? members
                    : AchievementRuleWiring.FindConditions(component).Select(m => m.Name).ToArray();

                if (members.Length == 0)
                {
                    EditorGUILayout.HelpBox(draft.SourceIndex == 0
                        ? "This component exposes no UnityEvent. Pick a Button (onClick) or a script with a public UnityEvent field, or hook into a bool condition instead."
                        : "This component has no bool method, property or field. Add e.g. `public bool BossDefeated => ...;` to your script and it will show up here.", MessageType.None);
                    return;
                }

                draft.MemberIndex = Mathf.Clamp(draft.MemberIndex, 0, members.Length - 1);
                draft.MemberIndex = EditorGUILayout.Popup("Member", draft.MemberIndex, members);

                if (rule.Kind == AchievementRuleKind.Counter && role == "trigger")
                    draft.Amount = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Counts as", "How much each trigger adds to the counter."), draft.Amount));

                if (draft.SourceIndex == 1)
                    EditorGUILayout.LabelField("Fires each time the value turns true (checked 5 times a second). With IL2CPP code stripping, mark the member [UnityEngine.Scripting.Preserve].", _styles.Mini);

                if (GUILayout.Button("Bind", GUILayout.Height(24)))
                    BindDraft(rule, role, draft, component, memberNames[draft.MemberIndex]);
            }
        }

        private static string[] ComponentLabels(List<Component> components)
        {
            var seen = new Dictionary<string, int>();
            return components.Select(c =>
            {
                string name = c.GetType().Name;
                seen[name] = seen.TryGetValue(name, out int count) ? count + 1 : 1;
                return seen[name] > 1 ? name + " #" + seen[name] : name;
            }).ToArray();
        }

        private void BindDraft(DashboardRule rule, string role, BindingDraft draft, Component component, string member)
        {
            bool isEvent = draft.SourceIndex == 0;
            var binding = new DashboardBinding
            {
                Role = role,
                Source = isEvent ? DashboardBindingSource.UnityEvent : DashboardBindingSource.Condition,
                ScenePath = component.gameObject.scene.path,
                ObjectPath = AchievementRuleWiring.ObjectPath(component.gameObject),
                ComponentType = component.GetType().Name,
                Member = member,
                Amount = Mathf.Max(1, draft.Amount),
            };

            if (rule.Bindings.Any(b => b.SameHookup(binding)))
            {
                SetStatus("That hookup already exists on this rule.", MessageType.Warning);
                return;
            }

            string eventName = rule.EventName(role);
            string error = isEvent
                ? AchievementRuleWiring.BindEvent(component, member, eventName, binding)
                : AchievementRuleWiring.BindCondition(component, member, eventName, binding);
            if (error != null)
            {
                SetStatus(error, MessageType.Warning);
                return;
            }

            rule.Bindings.Add(binding);
            draft.Target = null;
            draft.ResetPicks();
            MarkDirty();
            RefreshBindingScan();
            SetStatus("Hooked " + binding.Describe() + " to '" + eventName + "'. Save the scene (Ctrl+S) to keep the trigger, and write rules.json.", MessageType.Info);
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
