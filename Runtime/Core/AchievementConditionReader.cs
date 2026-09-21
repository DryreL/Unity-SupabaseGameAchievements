using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DryreLHub.SupabaseGameAchievements
{
    public enum AchievementConditionMemberKind
    {
        Method,
        Property,
        Field,
    }

    public readonly struct AchievementConditionMember
    {
        public AchievementConditionMember(string name, AchievementConditionMemberKind kind)
        {
            Name = name;
            Kind = kind;
        }

        public string Name { get; }

        public AchievementConditionMemberKind Kind { get; }

        public override string ToString() => Kind == AchievementConditionMemberKind.Method ? Name + "()" : Name;
    }

    /// <summary>
    /// Finds and reads the "condition" members a game script can offer the achievement rules: a
    /// <c>bool</c> method with no parameters, a <c>bool</c> property, or a <c>bool</c> field. The rule fires
    /// when the value turns true, so a script only has to expose <c>public bool BossDefeated =&gt; ...</c>.
    /// </summary>
    public static class AchievementConditionReader
    {
        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Engine base classes whose members are noise for a game script's own conditions.
        private static readonly HashSet<string> FrameworkBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Object",
            "UnityEngine.Object",
            "UnityEngine.Component",
            "UnityEngine.Behaviour",
            "UnityEngine.MonoBehaviour",
            "UnityEngine.ScriptableObject",
            "UnityEngine.EventSystems.UIBehaviour",
        };

        /// <summary>The type and its base types, most derived first, stopping before the engine's own base classes.</summary>
        public static IEnumerable<Type> ScriptTypes(Type type)
        {
            for (var t = type; t != null && !FrameworkBases.Contains(t.FullName ?? ""); t = t.BaseType)
                yield return t;
        }

        public static IReadOnlyList<AchievementConditionMember> FindMembers(Type type)
        {
            var found = new List<AchievementConditionMember>();
            if (type == null) return found;

            foreach (var t in ScriptTypes(type))
            {
                foreach (var m in t.GetMethods(Declared))
                    if (!m.IsSpecialName && !m.IsGenericMethodDefinition && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0)
                        found.Add(new AchievementConditionMember(m.Name, AchievementConditionMemberKind.Method));

                foreach (var p in t.GetProperties(Declared))
                    if (p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                        found.Add(new AchievementConditionMember(p.Name, AchievementConditionMemberKind.Property));

                foreach (var f in t.GetFields(Declared))
                    if (f.FieldType == typeof(bool) && !f.IsSpecialName && f.Name.IndexOf('<') < 0) // skip compiler backing fields
                        found.Add(new AchievementConditionMember(f.Name, AchievementConditionMemberKind.Field));
            }

            return found
                .GroupBy(m => m.Name)
                .Select(g => g.First())
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Builds a cheap reader for one member of <paramref name="target"/>; false with a reason when it does not qualify.</summary>
        public static bool TryCreate(object target, string member, out Func<bool> reader, out string error)
        {
            reader = null;
            error = null;
            if (target == null) { error = "The condition has no target object."; return false; }
            if (string.IsNullOrEmpty(member)) { error = "The condition has no member name."; return false; }

            foreach (var t in ScriptTypes(target.GetType()))
            {
                var method = t.GetMethods(Declared).FirstOrDefault(m =>
                    m.Name == member && !m.IsGenericMethodDefinition && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0);
                if (method != null)
                {
                    reader = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), target, method);
                    return true;
                }

                var property = t.GetProperties(Declared).FirstOrDefault(p =>
                    p.Name == member && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null);
                if (property != null)
                {
                    reader = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), target, property.GetGetMethod(true));
                    return true;
                }

                var field = t.GetFields(Declared).FirstOrDefault(f => f.Name == member && f.FieldType == typeof(bool));
                if (field != null)
                {
                    reader = () => (bool)field.GetValue(target);
                    return true;
                }
            }

            error = "'" + member + "' was not found on " + target.GetType().Name + " as a bool method (no parameters), property or field. " +
                "It may have been renamed or removed, or stripped from a build: mark it [UnityEngine.Scripting.Preserve] if you build with IL2CPP stripping.";
            return false;
        }
    }

    /// <summary>Turns a polled boolean into events: true only on the update where it goes from false to true.</summary>
    public sealed class AchievementConditionEdge
    {
        private bool _previous;

        /// <summary>A condition that is already true on the first look counts as rising once.</summary>
        public bool Rising(bool current)
        {
            bool rising = current && !_previous;
            _previous = current;
            return rising;
        }

        public void Reset() => _previous = false;
    }
}
