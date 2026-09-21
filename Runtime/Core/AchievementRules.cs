using System;
using System.Collections.Generic;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>Where <see cref="AchievementRules"/> keeps counter values between sessions.</summary>
    public interface IAchievementProgressStore
    {
        /// <summary>The stored value, or 0 when nothing was stored.</summary>
        int Get(string key);

        void Set(string key, int value);
    }

    /// <summary>A progress store that buffers writes and needs an explicit flush (e.g. PlayerPrefs, which only hits disk on Save).</summary>
    public interface IFlushableAchievementProgressStore : IAchievementProgressStore
    {
        /// <summary>Writes anything buffered to durable storage. Cheap when nothing changed.</summary>
        void Flush();
    }

    /// <summary>Progress that lives only as long as the process (tests, or a game that saves progress itself).</summary>
    public sealed class InMemoryAchievementProgressStore : IAchievementProgressStore
    {
        private readonly Dictionary<string, int> _values = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Get(string key) => _values.TryGetValue(key, out int value) ? value : 0;

        public void Set(string key, int value) => _values[key] = value;
    }

    /// <summary>Adapts the game's own save system: hand it two delegates and progress is saved wherever the game saves.</summary>
    public sealed class DelegateAchievementProgressStore : IAchievementProgressStore
    {
        private readonly Func<string, int> _get;
        private readonly Action<string, int> _set;

        public DelegateAchievementProgressStore(Func<string, int> get, Action<string, int> set)
        {
            _get = get ?? throw new ArgumentNullException(nameof(get));
            _set = set ?? throw new ArgumentNullException(nameof(set));
        }

        public int Get(string key) => _get(key);

        public void Set(string key, int value) => _set(key, value);
    }

    /// <summary>How far a counter achievement has come.</summary>
    public readonly struct AchievementProgress
    {
        public AchievementProgress(int current, int target)
        {
            Current = current;
            Target = target;
        }

        public int Current { get; }

        public int Target { get; }

        public bool IsComplete => Target > 0 && Current >= Target;

        /// <summary>0..1, for progress bars.</summary>
        public float Fraction => Target <= 0 ? 0f : Math.Min(1f, (float)Current / Target);

        public override string ToString() => Current + "/" + Target;
    }

    /// <summary>What a rule receives when gameplay reports an event.</summary>
    public readonly struct AchievementRuleEvent
    {
        public AchievementRuleEvent(AchievementRules rules, string name, int amount)
        {
            Rules = rules;
            Name = name;
            Amount = amount;
        }

        public AchievementRules Rules { get; }

        public string Name { get; }

        /// <summary>How much this report counts for (1 for a plain click, e.g. 250 for "gold collected").</summary>
        public int Amount { get; }

        public bool Unlock(string achievementKey) => Rules.Unlock(achievementKey);

        public int Add(string achievementKey) => Rules.Add(achievementKey, Amount);
    }

    /// <summary>
    /// The one place a game's achievement conditions live. Gameplay code only reports what happened
    /// (<c>rules.Report("enemy_killed")</c>); the rules decide whether that unlocks anything. Nothing in
    /// gameplay needs to know an achievement key, a target or whether it was already earned.
    /// </summary>
    /// <remarks>
    /// <para>Three kinds of rule cover the usual cases:</para>
    /// <list type="bullet">
    /// <item><description><see cref="UnlockOn"/>: an event unlocks an achievement ("opened the shop").</description></item>
    /// <item><description><see cref="Counter"/>: an event adds to a persisted counter that unlocks at a target ("click 5 times", "defeat 100 enemies").</description></item>
    /// <item><description><see cref="Run"/>: a start / fail / complete cycle that only unlocks when it completes without failing ("finish the quest without dying").</description></item>
    /// </list>
    /// <para>Anything else is <see cref="On"/>: a handler that runs your own condition. Achievements can never
    /// be revoked, so a failed attempt simply unlocks nothing. Progress only ever grows. A rule that throws is
    /// logged and skipped: a broken condition must never break gameplay. Call from the main thread.</para>
    /// </remarks>
    public sealed class AchievementRules
    {
        private const string DefaultProgressPrefix = "achievement.progress.";

        private readonly Func<string, bool> _unlock;
        private readonly Func<string, bool> _isUnlocked;
        private readonly IAchievementProgressStore _progress;
        private readonly IAchievementLogger _logger;
        private readonly string _progressPrefix;

        private readonly Dictionary<string, int> _targets = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Action<AchievementRuleEvent>>> _handlers =
            new Dictionary<string, List<Action<AchievementRuleEvent>>>(StringComparer.Ordinal);

        /// <param name="unlock">Unlocks an achievement by key; returns true only when it was newly unlocked (<c>AchievementManager.TryUnlock</c>).</param>
        /// <param name="isUnlocked">Whether the achievement is already unlocked (<c>AchievementManager.HasUnlocked</c>).</param>
        /// <param name="progress">Where counters are kept between sessions.</param>
        public AchievementRules(Func<string, bool> unlock, Func<string, bool> isUnlocked, IAchievementProgressStore progress,
            IAchievementLogger logger = null, string progressKeyPrefix = DefaultProgressPrefix)
        {
            _unlock = unlock ?? throw new ArgumentNullException(nameof(unlock));
            _isUnlocked = isUnlocked ?? throw new ArgumentNullException(nameof(isUnlocked));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _logger = logger ?? NullAchievementLogger.Instance;
            _progressPrefix = progressKeyPrefix ?? DefaultProgressPrefix;
        }

        /// <summary>Rules wired to the static <see cref="AchievementManager"/>.</summary>
        public static AchievementRules ForManager(IAchievementProgressStore progress, IAchievementLogger logger = null) =>
            new AchievementRules(AchievementManager.TryUnlock, AchievementManager.HasUnlocked, progress, logger ?? AchievementManager.Logger);

        // ------------------------------------------------------------------
        // Declaring rules (fluent, so a whole game's rules read as one list)
        // ------------------------------------------------------------------

        /// <summary>When <paramref name="onEvent"/> is reported, unlock the achievement.</summary>
        public AchievementRules UnlockOn(string achievementKey, string onEvent)
        {
            RequireName(achievementKey, nameof(achievementKey));
            return On(onEvent, e => e.Unlock(achievementKey));
        }

        /// <summary>
        /// A counter achievement: unlocks once its counter reaches <paramref name="target"/>. If
        /// <paramref name="onEvent"/> is given, every report of that event adds its amount; otherwise drive it
        /// yourself with <see cref="Add"/>. The counter is saved, so "5 clicks" survives a restart.
        /// </summary>
        public AchievementRules Counter(string achievementKey, int target, string onEvent = null)
        {
            RequireName(achievementKey, nameof(achievementKey));
            if (target < 1) throw new ArgumentOutOfRangeException(nameof(target), "A counter's target must be at least 1.");
            if (_targets.ContainsKey(achievementKey))
                throw new InvalidOperationException("A counter for '" + achievementKey + "' is already registered.");

            _targets[achievementKey] = target;
            if (!string.IsNullOrEmpty(onEvent)) On(onEvent, e => e.Add(achievementKey));
            return this;
        }

        /// <summary>
        /// "Do it without failing": <paramref name="startEvent"/> begins an attempt, <paramref name="failEvent"/>
        /// spoils it, and <paramref name="completeEvent"/> unlocks the achievement only if the attempt was not
        /// spoiled. Every completion or failure ends the attempt; the next start begins a fresh one. Pass a null
        /// <paramref name="startEvent"/> if an attempt should simply be "since the last completion". Attempts are
        /// not saved: they last as long as the session.
        /// </summary>
        public AchievementRules Run(string achievementKey, string startEvent, string failEvent, string completeEvent)
        {
            RequireName(achievementKey, nameof(achievementKey));
            RequireName(completeEvent, nameof(completeEvent));

            bool active = startEvent == null;
            bool failed = false;

            if (startEvent != null)
                On(startEvent, _ => { active = true; failed = false; });
            if (failEvent != null)
                On(failEvent, _ => { if (active) failed = true; });
            On(completeEvent, e =>
            {
                bool earned = active && !failed;
                active = startEvent == null;
                failed = false;
                if (earned) e.Unlock(achievementKey);
            });
            return this;
        }

        /// <summary>Runs <paramref name="rule"/> whenever <paramref name="eventName"/> is reported, for conditions the built-in rules do not cover.</summary>
        public AchievementRules On(string eventName, Action<AchievementRuleEvent> rule)
        {
            RequireName(eventName, nameof(eventName));
            if (rule == null) throw new ArgumentNullException(nameof(rule));

            if (!_handlers.TryGetValue(eventName, out var list))
                _handlers[eventName] = list = new List<Action<AchievementRuleEvent>>();
            list.Add(rule);
            return this;
        }

        // ------------------------------------------------------------------
        // Reporting (what gameplay calls)
        // ------------------------------------------------------------------

        /// <summary>Tells the rules something happened. Unknown events are ignored; a rule that throws is logged and skipped.</summary>
        /// <param name="amount">How much it counts for. Values below 1 are ignored.</param>
        public void Report(string eventName, int amount = 1)
        {
            if (string.IsNullOrEmpty(eventName) || amount < 1) return;
            if (!_handlers.TryGetValue(eventName, out var handlers)) return;

            var raised = new AchievementRuleEvent(this, eventName, amount);
            // Copy: a rule may register more rules, and one throwing must not stop the rest.
            foreach (var handler in handlers.ToArray())
            {
                try
                {
                    handler(raised);
                }
                catch (Exception e)
                {
                    _logger.Error("Achievement rule for event '" + eventName + "' failed: " + e);
                }
            }
        }

        /// <summary>Unlocks the achievement now (a no-op that returns false if it already is). Rules use this; gameplay may too.</summary>
        public bool Unlock(string achievementKey) => !string.IsNullOrEmpty(achievementKey) && _unlock(achievementKey);

        /// <summary>
        /// Adds to a registered counter and unlocks the achievement when the target is reached. Returns the new
        /// counter value (clamped at the target). Once the achievement is unlocked the counter stops.
        /// </summary>
        public int Add(string achievementKey, int amount = 1)
        {
            if (!_targets.TryGetValue(achievementKey ?? "", out int target))
            {
                _logger.Warning("Add('" + achievementKey + "') ignored: no counter is registered for it.");
                return 0;
            }

            if (_isUnlocked(achievementKey)) return target;
            if (amount < 1) return Math.Min(_progress.Get(ProgressKey(achievementKey)), target);

            long next = Math.Min((long)target, (long)_progress.Get(ProgressKey(achievementKey)) + amount);
            _progress.Set(ProgressKey(achievementKey), (int)next);
            if (next >= target) _unlock(achievementKey);
            return (int)next;
        }

        // ------------------------------------------------------------------
        // Reading progress (for UI: "3/5")
        // ------------------------------------------------------------------

        /// <summary>Progress of a counter achievement; an unlocked one always reports as complete.</summary>
        public AchievementProgress GetProgress(string achievementKey)
        {
            if (!_targets.TryGetValue(achievementKey ?? "", out int target)) return new AchievementProgress(0, 0);
            int current = _isUnlocked(achievementKey) ? target : Math.Min(_progress.Get(ProgressKey(achievementKey)), target);
            return new AchievementProgress(current, target);
        }

        /// <summary>Sets a counter back to 0 (for a "new game"). Does not touch an achievement that is already unlocked.</summary>
        public void ResetProgress(string achievementKey)
        {
            if (_targets.ContainsKey(achievementKey ?? "")) _progress.Set(ProgressKey(achievementKey), 0);
        }

        private string ProgressKey(string achievementKey) => _progressPrefix + achievementKey;

        private static void RequireName(string value, string parameter)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("A name is required.", parameter);
        }
    }
}
