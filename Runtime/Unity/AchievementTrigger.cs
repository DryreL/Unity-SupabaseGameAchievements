using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Reports one achievement event when <see cref="Fire"/> is called. The Achievement Dashboard adds it next
    /// to a Button (or any UnityEvent) and wires the event to <see cref="Fire"/>; you can also add it yourself
    /// and bind <see cref="Fire"/> from an Inspector event.
    /// </summary>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Trigger")]
    public sealed class AchievementTrigger : MonoBehaviour
    {
        [Tooltip("The event to report. The Achievement Dashboard fills this in from the rule it belongs to.")]
        [SerializeField] private string _eventName = "";
        [Tooltip("How much each Fire counts for (counters add this much).")]
        [SerializeField, Min(1)] private int _amount = 1;
        [Tooltip("Written by the Achievement Dashboard so it can find and remove this trigger again. Leave as is.")]
        [SerializeField] private string _bindingId = "";

        public string EventName => _eventName;

        public int Amount => _amount;

        public string BindingId => _bindingId;

        /// <summary>Reports the event once. Shaped for a UnityEvent: no arguments.</summary>
        public void Fire() => AchievementEvents.Report(_eventName, _amount);
    }
}
