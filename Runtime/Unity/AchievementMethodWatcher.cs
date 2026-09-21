using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Watches a <c>bool</c> method, property or field on one of your own scripts and reports an achievement
    /// event each time it turns true (false to true). Added by the Achievement Dashboard for "Condition"
    /// bindings, so a script only needs to expose <c>public bool BossDefeated =&gt; ...</c>.
    /// </summary>
    /// <remarks>
    /// The member is looked up by name at runtime. In IL2CPP builds with managed stripping the linker can remove
    /// a member nothing else references; mark it <c>[UnityEngine.Scripting.Preserve]</c> to keep it.
    /// </remarks>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Condition Watcher")]
    public sealed class AchievementMethodWatcher : MonoBehaviour
    {
        [SerializeField] private Component _target;
        [Tooltip("Name of a bool method (no parameters), property or field on the target.")]
        [SerializeField] private string _member = "";
        [SerializeField] private string _eventName = "";
        [SerializeField, Min(1)] private int _amount = 1;
        [Tooltip("Seconds between checks. Real time, so it keeps working while the game is paused.")]
        [SerializeField, Min(0.02f)] private float _interval = 0.2f;
        [SerializeField] private string _bindingId = "";

        private System.Func<bool> _reader;
        private readonly AchievementConditionEdge _edge = new AchievementConditionEdge();
        private float _nextCheck;
        private bool _reportedProblem;

        public string EventName => _eventName;

        public string BindingId => _bindingId;

        public string Member => _member;

        public Component Target => _target;

        private void OnEnable()
        {
            _edge.Reset();
            _nextCheck = 0f;
            _reader = null;
            _reportedProblem = false;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + _interval;

            if (_reader == null && !TryResolve()) return;

            bool value;
            try
            {
                value = _reader();
            }
            catch (System.Exception e)
            {
                // A throwing condition in a game script is reported once and never breaks the scene.
                if (!_reportedProblem)
                {
                    _reportedProblem = true;
                    Debug.LogException(e, this);
                }
                return;
            }

            if (_edge.Rising(value)) AchievementEvents.Report(_eventName, _amount);
        }

        private bool TryResolve()
        {
            if (AchievementConditionReader.TryCreate(_target, _member, out _reader, out string error)) return true;

            if (!_reportedProblem)
            {
                _reportedProblem = true;
                Debug.LogWarning("[Achievements] Condition '" + _member + "' for event '" + _eventName + "' is not available: " + error, this);
            }
            enabled = false; // nothing to poll; the dashboard's Rules tab shows the binding as broken
            return false;
        }
    }
}
