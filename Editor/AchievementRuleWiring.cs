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

        /// <summary>The components on an object a rule can hook into (ours are left out).</summary>
        public static List<Component> HookableComponents(GameObject go) =>
            go == null
                ? new List<Component>()
                : go.GetComponents<Component>().Where(c => c != null && !(c is AchievementTrigger) && !(c is AchievementMethodWatcher)).ToList();

        // ------------------------------------------------------------------
        // What is in the open scenes
        // ------------------------------------------------------------------

        /// <summary>Every trigger and watcher in the open scenes, by the binding id the dashboard stamped on it.</summary>
        public static Dictionary<string, Object> Scan()
        {
            var map = new Dictionary<string, Object>(StringComparer.Ordinal);
            foreach (var trigger in Object.FindObjectsByType<AchievementTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(trigger.BindingId)) map[trigger.BindingId] = trigger;
            foreach (var watcher in Object.FindObjectsByType<AchievementMethodWatcher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!string.IsNullOrEmpty(watcher.BindingId)) map[watcher.BindingId] = watcher;
            return map;
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

        /// <summary>Removes the trigger/watcher (and the UnityEvent listener that called it). False if it is not in an open scene.</summary>
        public static bool Unbind(string bindingId)
        {
            if (!Scan().TryGetValue(bindingId, out var found) || found == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Unbind achievement rule");
            int group = Undo.GetCurrentGroup();

            var go = ((Component)found).gameObject;
            if (found is AchievementTrigger)
            {
                foreach (var component in go.GetComponents<Component>().Where(c => c != null))
                {
                    foreach (var member in FindEvents(component))
                    {
                        // Backwards: removing an entry shifts the ones after it.
                        for (int i = member.Event.GetPersistentEventCount() - 1; i >= 0; i--)
                        {
                            if (member.Event.GetPersistentTarget(i) != found) continue;
                            Undo.RecordObject(component, "Unbind achievement rule");
                            UnityEventTools.RemovePersistentListener(member.Event, i);
                            Touch(component);
                        }
                    }
                }
            }

            Undo.DestroyObjectImmediate(found);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Undo.CollapseUndoOperations(group);
            return true;
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
