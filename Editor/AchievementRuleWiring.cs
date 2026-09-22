using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>
    /// Adds and removes the scene components that drive a rule: an <see cref="AchievementTrigger"/> wired to a
    /// UnityEvent, or an <see cref="AchievementMethodWatcher"/> watching a bool member of one of your scripts.
    /// Everything is recorded with Undo and marks the scene dirty; only objects of scenes that are currently
    /// open can be changed.
    /// </summary>
    internal static class AchievementRuleWiring
    {
        internal readonly struct EventMember
        {
            public EventMember(string name, UnityEventBase unityEvent)
            {
                Name = name;
                Event = unityEvent;
            }

            public string Name { get; }

            public UnityEventBase Event { get; }
        }

        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------

        /// <summary>The UnityEvents a component exposes: public or [SerializeField] fields, and public properties (a Button's onClick).</summary>
        public static List<EventMember> FindEvents(Component component) =>
            component == null ? new List<EventMember>() : FindEventsOn(component);

        /// <summary>Same as <see cref="FindEvents"/> for any object, so the lookup can be tested on plain classes.</summary>
        internal static List<EventMember> FindEventsOn(object owner)
        {
            var found = new List<EventMember>();

            foreach (var type in AchievementConditionReader.ScriptTypes(owner.GetType()))
            {
                // Properties before fields: a Button's public "onClick" and its private "m_OnClick" backing field are
                // the same event, and the public name is the one worth showing.
                foreach (var property in type.GetProperties(Declared))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(property.PropertyType)) continue;
                    if (property.GetIndexParameters().Length > 0 || property.GetGetMethod(false) == null) continue;
                    if (TryRead(() => property.GetValue(owner)) is UnityEventBase unityEvent) found.Add(new EventMember(property.Name, unityEvent));
                }

                foreach (var field in type.GetFields(Declared))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(field.FieldType)) continue;
                    if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;
                    if (TryRead(() => field.GetValue(owner)) is UnityEventBase unityEvent) found.Add(new EventMember(field.Name, unityEvent));
                }
            }

            var unique = new List<EventMember>();
            foreach (var member in found)
                if (!unique.Any(u => u.Name == member.Name || ReferenceEquals(u.Event, member.Event))) unique.Add(member);
            return unique.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        }

        // A property getter can throw for an object in an odd state; that member is simply not offered.
        private static object TryRead(Func<object> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static IReadOnlyList<AchievementConditionMember> FindConditions(Component component) =>
            component == null ? new List<AchievementConditionMember>() : AchievementConditionReader.FindMembers(component.GetType());

        /// <summary>The components directly on an object a rule can hook into (ours are left out).</summary>
        public static List<Component> HookableComponents(GameObject go) =>
            go == null
                ? new List<Component>()
                : go.GetComponents<Component>().Where(c => c != null && !(c is AchievementTrigger) && !(c is AchievementMethodWatcher)).ToList();

        /// <summary>
        /// Same as <see cref="HookableComponents"/>, but also looks at every child (a trigger collider or a button is
        /// often nested a level or two down, e.g. under a prefab's visual root). The object's own components come
        /// first, in hierarchy order, so a direct hit still wins over a deeper one.
        /// </summary>
        public static List<Component> HookableComponentsInHierarchy(GameObject go) =>
            go == null
                ? new List<Component>()
                : go.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is AchievementTrigger) && !(c is AchievementMethodWatcher)).ToList();

        // ------------------------------------------------------------------
        // What is in the open scenes
        // ------------------------------------------------------------------

        /// <summary>
        /// Every trigger and watcher that can be reached without opening anything, by the binding id the dashboard
        /// stamped on it: the ones in open scenes and the open prefab stage, then the ones inside the given prefab
        /// assets (read straight from the asset, so a prefab that is not open still counts).
        /// </summary>
        public static Dictionary<string, Object> Scan(IEnumerable<string> prefabPaths = null)
        {
            var map = new Dictionary<string, Object>(StringComparer.Ordinal);
            foreach (var trigger in Object.FindObjectsByType<AchievementTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(trigger.BindingId)) map[trigger.BindingId] = trigger;
            foreach (var watcher in Object.FindObjectsByType<AchievementMethodWatcher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(watcher.BindingId)) map[watcher.BindingId] = watcher;

            if (prefabPaths != null)
            {
                foreach (string path in prefabPaths.Where(IsPrefabPath).Distinct())
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (asset == null) continue;

                    foreach (var trigger in asset.GetComponentsInChildren<AchievementTrigger>(true))
                        if (!string.IsNullOrEmpty(trigger.BindingId) && !map.ContainsKey(trigger.BindingId)) map[trigger.BindingId] = trigger;
                    foreach (var watcher in asset.GetComponentsInChildren<AchievementMethodWatcher>(true))
                        if (!string.IsNullOrEmpty(watcher.BindingId) && !map.ContainsKey(watcher.BindingId)) map[watcher.BindingId] = watcher;
                }
            }
            return map;
        }

        // ------------------------------------------------------------------
        // Prefabs and scenes that are not open
        // ------------------------------------------------------------------

        /// <summary>A binding made in prefab mode records the prefab asset's path where a scene binding records the scene's.</summary>
        public static bool IsPrefabPath(string path) => !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when what holds the trigger can be inspected right now: the scene is open, or the prefab asset exists.</summary>
        public static bool IsContainerAvailable(string path) =>
            IsPrefabPath(path) ? AssetDatabase.LoadAssetAtPath<GameObject>(path) != null : IsSceneLoaded(path);

        private sealed class ClosedFileEntry
        {
            public DateTime Stamp;
            public bool IsText;
            public HashSet<string> BindingIds = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly Dictionary<string, ClosedFileEntry> ClosedFiles = new Dictionary<string, ClosedFileEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex BindingIdPattern =
            new System.Text.RegularExpressions.Regex("_bindingId: ([0-9a-fA-F]{8})", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Looks for a binding id in a scene file that is not open, by reading its text (cached until the file changes).
        /// <paramref name="verifiable"/> is false when the file cannot be read as text (binary serialization), in which
        /// case "not found" means nothing.
        /// </summary>
        public static bool ClosedFileContains(string assetPath, string bindingId, out bool verifiable)
        {
            verifiable = false;
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Directory.GetParent(Application.dataPath).FullName, assetPath));
            if (!System.IO.File.Exists(full)) return false;

            var stamp = System.IO.File.GetLastWriteTimeUtc(full);
            if (!ClosedFiles.TryGetValue(full, out var entry) || entry.Stamp != stamp)
            {
                entry = new ClosedFileEntry { Stamp = stamp };
                try
                {
                    string text = System.IO.File.ReadAllText(full);
                    entry.IsText = text.StartsWith("%YAML", StringComparison.Ordinal);
                    if (entry.IsText)
                        foreach (System.Text.RegularExpressions.Match match in BindingIdPattern.Matches(text)) entry.BindingIds.Add(match.Groups[1].Value);
                }
                catch (Exception)
                {
                    entry.IsText = false; // unreadable right now: report "cannot verify", not "missing"
                }
                ClosedFiles[full] = entry;
            }

            verifiable = entry.IsText;
            return entry.BindingIds.Contains(bindingId);
        }

        public static string ObjectPath(GameObject go)
        {
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        public static bool IsSceneLoaded(string scenePath) =>
            !string.IsNullOrEmpty(scenePath) && UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath).isLoaded;

        // ------------------------------------------------------------------
        // Binding
        // ------------------------------------------------------------------

        /// <summary>Wires <paramref name="source"/>'s UnityEvent <paramref name="member"/> to a new trigger. Returns an error message, or null.</summary>
        public static string BindEvent(Component source, string member, string eventName, DashboardBinding binding)
        {
            string problem = CheckSceneObject(source);
            if (problem != null) return problem;

            var target = FindEvents(source).FirstOrDefault(e => e.Name == member);
            if (target.Event == null) return "'" + member + "' is not a UnityEvent on " + source.GetType().Name + ".";

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Bind achievement rule");
            int group = Undo.GetCurrentGroup();

            var trigger = Undo.AddComponent<AchievementTrigger>(source.gameObject);
            var serialized = new SerializedObject(trigger);
            serialized.FindProperty("_eventName").stringValue = eventName;
            serialized.FindProperty("_amount").intValue = Math.Max(1, binding.Amount);
            serialized.FindProperty("_bindingId").stringValue = binding.Id;
            serialized.ApplyModifiedProperties();

            Undo.RecordObject(source, "Bind achievement rule");
            UnityEventTools.AddVoidPersistentListener(target.Event, new UnityAction(trigger.Fire));
            Touch(source);

            Undo.CollapseUndoOperations(group);
            return null;
        }

        /// <summary>Adds a watcher for a bool member of <paramref name="source"/>. Returns an error message, or null.</summary>
        public static string BindCondition(Component source, string member, string eventName, DashboardBinding binding)
        {
            string problem = CheckSceneObject(source);
            if (problem != null) return problem;
            if (!AchievementConditionReader.TryCreate(source, member, out _, out string error)) return error;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Bind achievement rule");
            int group = Undo.GetCurrentGroup();

            var watcher = Undo.AddComponent<AchievementMethodWatcher>(source.gameObject);
            var serialized = new SerializedObject(watcher);
            serialized.FindProperty("_target").objectReferenceValue = source;
            serialized.FindProperty("_member").stringValue = member;
            serialized.FindProperty("_eventName").stringValue = eventName;
            serialized.FindProperty("_amount").intValue = Math.Max(1, binding.Amount);
            serialized.FindProperty("_bindingId").stringValue = binding.Id;
            serialized.ApplyModifiedProperties();
            Touch(watcher);

            Undo.CollapseUndoOperations(group);
            return null;
        }

        /// <summary>
        /// Removes the trigger/watcher (and the UnityEvent listener that called it), wherever it can be reached: an open
        /// scene, an open prefab stage, or a prefab asset (edited and saved in place). False if it is in a scene that is
        /// not open, or is not there any more.
        /// </summary>
        public static bool Unbind(DashboardBinding binding)
        {
            Component found;
            if (IsPrefabPath(binding.ScenePath))
            {
                // A copy of the trigger inside a scene's prefab instance cannot be deleted from the scene, so a prefab
                // binding is only ever removed through the prefab: its open stage, or the asset itself.
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null || stage.assetPath != binding.ScenePath) return UnbindInPrefabAsset(binding.ScenePath, binding.Id);
                found = FindBinding(stage.prefabContentsRoot, binding.Id);
            }
            else
            {
                found = Scan().TryGetValue(binding.Id, out var live) ? live as Component : null;
            }
            if (found == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Unbind achievement rule");
            int group = Undo.GetCurrentGroup();

            var go = found.gameObject;
            if (found is AchievementTrigger) RemoveListenersTo(found, go, record: true);

            Undo.DestroyObjectImmediate(found);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Undo.CollapseUndoOperations(group);
            return true;
        }

        private static Component FindBinding(GameObject root, string bindingId)
        {
            Component found = root.GetComponentsInChildren<AchievementTrigger>(true).FirstOrDefault(t => t.BindingId == bindingId);
            return found != null ? found : root.GetComponentsInChildren<AchievementMethodWatcher>(true).FirstOrDefault(w => w.BindingId == bindingId);
        }

        private static void RemoveListenersTo(Object trigger, GameObject go, bool record)
        {
            foreach (var component in go.GetComponents<Component>().Where(c => c != null))
            {
                foreach (var member in FindEvents(component))
                {
                    // Backwards: removing an entry shifts the ones after it.
                    for (int i = member.Event.GetPersistentEventCount() - 1; i >= 0; i--)
                    {
                        if (member.Event.GetPersistentTarget(i) != trigger) continue;
                        if (record) Undo.RecordObject(component, "Unbind achievement rule");
                        UnityEventTools.RemovePersistentListener(member.Event, i);
                        if (record) Touch(component);
                    }
                }
            }
        }

        private static bool UnbindInPrefabAsset(string prefabPath, string bindingId)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return false;

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var target = FindBinding(root, bindingId);
                if (target == null) return false;

                if (target is AchievementTrigger) RemoveListenersTo(target, target.gameObject, record: false);
                Object.DestroyImmediate(target);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string CheckSceneObject(Component source)
        {
            if (source == null) return "Pick an object first.";
            if (EditorUtility.IsPersistent(source)) return "Pick an object from a scene (drag it from the Hierarchy), not a prefab asset. Open the prefab or the scene that contains it.";
            if (string.IsNullOrEmpty(source.gameObject.scene.path)) return "Save the scene first: a rule has to remember which scene the trigger is in.";
            return null;
        }

        private static void Touch(Object changed)
        {
            EditorUtility.SetDirty(changed);
            PrefabUtility.RecordPrefabInstancePropertyModifications(changed);
            var component = changed as Component;
            if (component != null) EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }
    }
}
