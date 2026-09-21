using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>The three shapes of rule the Achievement Dashboard can author (see <see cref="AchievementRules"/>).</summary>
    public enum AchievementRuleKind
    {
        /// <summary>The trigger unlocks the achievement.</summary>
        Unlock = 0,

        /// <summary>Every trigger adds to a saved counter; reaching the target unlocks the achievement.</summary>
        Counter = 1,

        /// <summary>A start / fail / complete cycle that unlocks only when it completes without failing.</summary>
        Run = 2,
    }

    /// <summary>One authored rule: which achievement, what kind, and (for counters) how many triggers it takes.</summary>
    public sealed class AchievementRuleDefinition
    {
        public AchievementRuleDefinition(string id, string achievementKey, AchievementRuleKind kind, int target = 1)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A rule id is required.", nameof(id));
            if (string.IsNullOrEmpty(achievementKey)) throw new ArgumentException("An achievement key is required.", nameof(achievementKey));
            if (kind == AchievementRuleKind.Counter && target < 1) throw new ArgumentOutOfRangeException(nameof(target), "A counter's target must be at least 1.");

            Id = id;
            AchievementKey = achievementKey;
            Kind = kind;
            Target = kind == AchievementRuleKind.Counter ? target : 0;
        }

        /// <summary>Stable id; the names of the events this rule listens to are derived from it and never change.</summary>
        public string Id { get; }

        public string AchievementKey { get; }

        public AchievementRuleKind Kind { get; }

        /// <summary>Counter rules only; 0 otherwise.</summary>
        public int Target { get; }

        /// <summary>The roles a trigger can be bound to for this kind of rule.</summary>
        public IReadOnlyList<string> Roles => RolesFor(Kind);

        /// <summary>The event name a trigger with this role reports (<c>role</c> is ignored except for Run rules).</summary>
        public string EventName(string role = null) => EventNameFor(Id, Kind, role);

        public static IReadOnlyList<string> RolesFor(AchievementRuleKind kind) =>
            kind == AchievementRuleKind.Run ? RunRoles : TriggerRoles;

        public static string EventNameFor(string id, AchievementRuleKind kind, string role) =>
            kind == AchievementRuleKind.Run ? id + ":" + (string.IsNullOrEmpty(role) ? "complete" : role) : id;

        private static readonly string[] TriggerRoles = { "trigger" };
        private static readonly string[] RunRoles = { "start", "fail", "complete" };
    }

    /// <summary>
    /// The set of authored rules a game ships as <c>rules.json</c>: written by the Achievement Dashboard,
    /// read at runtime and turned into <see cref="AchievementRules"/>. Scene triggers only report the event
    /// names derived from a rule's id, so editing a rule here never needs a scene change.
    /// </summary>
    public sealed class AchievementRuleSet
    {
        public const int SupportedFormatVersion = 1;

        public AchievementRuleSet(IEnumerable<AchievementRuleDefinition> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            var list = new List<AchievementRuleDefinition>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var counters = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (rule == null) throw new ArgumentException("The rule set contains a null rule.", nameof(rules));
                if (!ids.Add(rule.Id)) throw new AchievementRuleException("Duplicate rule id '" + rule.Id + "'.");
                // Two counters on one achievement would share (and double-count) one saved value.
                if (rule.Kind == AchievementRuleKind.Counter && !counters.Add(rule.AchievementKey))
                    throw new AchievementRuleException("Achievement '" + rule.AchievementKey + "' has more than one counter rule.");
                list.Add(rule);
            }
            Rules = list;
        }

        public IReadOnlyList<AchievementRuleDefinition> Rules { get; }

        /// <summary>Registers every rule with <paramref name="rules"/>.</summary>
        public void ApplyTo(AchievementRules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            foreach (var rule in Rules)
            {
                switch (rule.Kind)
                {
                    case AchievementRuleKind.Unlock:
                        rules.UnlockOn(rule.AchievementKey, rule.EventName());
                        break;
                    case AchievementRuleKind.Counter:
                        rules.Counter(rule.AchievementKey, rule.Target, rule.EventName());
                        break;
                    case AchievementRuleKind.Run:
                        rules.Run(rule.AchievementKey, rule.EventName("start"), rule.EventName("fail"), rule.EventName("complete"));
                        break;
                }
            }
        }

        /// <summary>Deterministic output (fixed key order, 2-space indent, trailing newline) so an unchanged set produces no diff.</summary>
        public string ToJson()
        {
            var array = new JArray();
            foreach (var rule in Rules)
            {
                var entry = new JObject
                {
                    ["id"] = rule.Id,
                    ["achievement"] = rule.AchievementKey,
                    ["kind"] = rule.Kind.ToString().ToLowerInvariant(),
                };
                if (rule.Kind == AchievementRuleKind.Counter) entry["target"] = rule.Target;
                array.Add(entry);
            }

            var root = new JObject { ["formatVersion"] = SupportedFormatVersion, ["rules"] = array };
            // Newtonsoft indents with Environment.NewLine (CRLF on Windows); LF keeps the file identical on every OS.
            return JsonConvert.SerializeObject(root, Formatting.Indented).Replace("\r\n", "\n") + "\n";
        }

        /// <summary>
        /// Parses <c>rules.json</c>. Structural problems throw <see cref="AchievementRuleException"/>; a rule of a
        /// kind this build does not know is skipped, so a newer dashboard never breaks an older game.
        /// </summary>
        public static AchievementRuleSet FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new AchievementRuleException("The rules file is empty.");

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    root = JObject.Load(reader);
            }
            catch (JsonException e)
            {
                throw new AchievementRuleException("The rules file is not valid JSON: " + e.Message, e);
            }

            int format = ReadInt(root, "formatVersion", 1);
            if (format > SupportedFormatVersion)
                throw new AchievementRuleException("The rules file format " + format + " is newer than the supported format " + SupportedFormatVersion + ".");
            if (!(root["rules"] is JArray items)) throw new AchievementRuleException("The rules file has no 'rules' array.");

            var definitions = new List<AchievementRuleDefinition>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (!(items[i] is JObject item)) throw new AchievementRuleException("rules[" + i + "] is not an object.");
                if (!TryParseKind(ReadString(item, "kind"), out var kind)) continue;

                try
                {
                    definitions.Add(new AchievementRuleDefinition(ReadString(item, "id"), ReadString(item, "achievement"), kind, ReadInt(item, "target", 1)));
                }
                catch (Exception e) when (e is ArgumentException || e is FormatException || e is OverflowException)
                {
                    throw new AchievementRuleException("rules[" + i + "] is invalid: " + e.Message, e);
                }
            }
            return new AchievementRuleSet(definitions);
        }

        private static bool TryParseKind(string value, out AchievementRuleKind kind)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "unlock": kind = AchievementRuleKind.Unlock; return true;
                case "counter": kind = AchievementRuleKind.Counter; return true;
                case "run": kind = AchievementRuleKind.Run; return true;
                default: kind = AchievementRuleKind.Unlock; return false;
            }
        }

        private static string ReadString(JObject obj, string name)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new AchievementRuleException("'" + name + "' must be a string.");
            return (string)token;
        }

        private static int ReadInt(JObject obj, string name, int fallback)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer) throw new AchievementRuleException("'" + name + "' must be an integer.");
            long value = Convert.ToInt64(((JValue)token).Value, CultureInfo.InvariantCulture);
            if (value < int.MinValue || value > int.MaxValue) throw new AchievementRuleException("'" + name + "' is out of range.");
            return (int)value;
        }
    }

    public sealed class AchievementRuleException : Exception
    {
        public AchievementRuleException(string message) : base(message) { }

        public AchievementRuleException(string message, Exception inner) : base(message, inner) { }
    }
}
