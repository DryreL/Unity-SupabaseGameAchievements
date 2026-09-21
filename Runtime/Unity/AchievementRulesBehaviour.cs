using System;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Base class for the one component that holds a game's achievement conditions. Derive from it, override
    /// <see cref="Configure"/> to declare the rules, and put it in your first scene:
    /// <code>
    /// public sealed class MyGameAchievementRules : AchievementRulesBehaviour
    /// {
    ///     protected override void Configure(AchievementRules rules)
    ///     {
    ///         rules.Counter("clicked_link_5_times", 5, onEvent: "link_clicked")
    ///              .Counter("centurion", 100, onEvent: "enemy_killed")
    ///              .UnlockOn("opened_shop", onEvent: "shop_opened")
    ///              .Run("flawless_quest", startEvent: "quest_started", failEvent: "player_died", completeEvent: "quest_done");
    ///     }
    /// }
    /// </code>
    /// Gameplay then only reports what happened: <c>AchievementRulesBehaviour.Report("enemy_killed")</c>, or an
    /// Inspector button/UnityEvent bound to <see cref="ReportEvent(string)"/> with the event name typed in.
    /// </summary>
    /// <remarks>
    /// The rules are wired to <see cref="AchievementManager"/>, so initialize the achievement system as usual.
    /// Counters are kept by <see cref="CreateProgressStore"/> (PlayerPrefs by default). There is one instance
    /// per game; a second one destroys itself.
    /// </remarks>
    public abstract class AchievementRulesBehaviour : MonoBehaviour
    {
        [Tooltip("Keep the rules alive across scene loads (recommended). Only applies to a root object.")]
        [SerializeField] private bool _dontDestroyOnLoad = true;

        private IAchievementProgressStore _store;

        public static AchievementRulesBehaviour Instance { get; private set; }

        /// <summary>The declared rules; null until this component has awakened.</summary>
        public AchievementRules Rules { get; private set; }

        /// <summary>Declares the game's rules. Called once, from Awake.</summary>
        protected abstract void Configure(AchievementRules rules);

        /// <summary>Where counters are saved. Override to use the game's own save system.</summary>
        protected virtual IAchievementProgressStore CreateProgressStore() => new PlayerPrefsAchievementProgressStore();

        // Domain reload can be switched off in the Editor; a stale static from the last play session must not survive.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        protected virtual void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (_dontDestroyOnLoad && transform.parent == null) DontDestroyOnLoad(gameObject);

            _store = CreateProgressStore();
            Rules = AchievementRules.ForManager(_store, new UnityAchievementLogger(false));
            AchievementEvents.Register(Rules);
            try
            {
                Configure(Rules);
            }
            catch (Exception e)
            {
                // A mistake in the rules (say, a counter registered twice) is reported, never allowed to break the scene.
                Debug.LogException(e);
            }
        }

        protected virtual void OnDestroy()
        {
            if (Rules != null) AchievementEvents.Unregister(Rules);
            FlushProgress();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) FlushProgress();
        }

        private void OnApplicationQuit() => FlushProgress();

        private void FlushProgress() => (_store as IFlushableAchievementProgressStore)?.Flush();

        /// <summary>
        /// Reports an event to every active set of rules (this component's and the Achievement Dashboard's).
        /// Same as <see cref="AchievementEvents.Report"/>.
        /// </summary>
        public static void Report(string eventName, int amount = 1) => AchievementEvents.Report(eventName, amount);

        /// <summary>Same as <see cref="Report"/> for one occurrence, shaped so a Button/UnityEvent can bind it and pass the event name.</summary>
        public void ReportEvent(string eventName) => AchievementEvents.Report(eventName, 1);

        /// <summary>Reports an event that counts for <paramref name="amount"/> (e.g. gold collected).</summary>
        public void ReportEvent(string eventName, int amount) => AchievementEvents.Report(eventName, amount);
    }
}
