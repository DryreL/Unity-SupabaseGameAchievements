using UnityEngine;
using UnityEngine.Events;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Turns this object's physics callbacks (trigger/collision enter and exit, 2D and 3D) into UnityEvents, so the
    /// Achievement Dashboard's "Hook into: UnityEvent" flow can bind an achievement to them the same way it binds
    /// a Button's onClick. Add this next to a Collider/Collider2D (and, for OnCollision*, a Rigidbody/Rigidbody2D).
    /// </summary>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Collider Events")]
    public sealed class AchievementColliderEvents : MonoBehaviour
    {
        [Header("3D Trigger")]
        [SerializeField] private UnityEvent _onTriggerEnter = new UnityEvent();
        [SerializeField] private UnityEvent _onTriggerExit = new UnityEvent();

        [Header("3D Collision")]
        [SerializeField] private UnityEvent _onCollisionEnter = new UnityEvent();
        [SerializeField] private UnityEvent _onCollisionExit = new UnityEvent();

        [Header("2D Trigger")]
        [SerializeField] private UnityEvent _onTriggerEnter2D = new UnityEvent();
        [SerializeField] private UnityEvent _onTriggerExit2D = new UnityEvent();

        [Header("2D Collision")]
        [SerializeField] private UnityEvent _onCollisionEnter2D = new UnityEvent();
        [SerializeField] private UnityEvent _onCollisionExit2D = new UnityEvent();

        public UnityEvent onTriggerEnter => _onTriggerEnter;
        public UnityEvent onTriggerExit => _onTriggerExit;
        public UnityEvent onCollisionEnter => _onCollisionEnter;
        public UnityEvent onCollisionExit => _onCollisionExit;
        public UnityEvent onTriggerEnter2D => _onTriggerEnter2D;
        public UnityEvent onTriggerExit2D => _onTriggerExit2D;
        public UnityEvent onCollisionEnter2D => _onCollisionEnter2D;
        public UnityEvent onCollisionExit2D => _onCollisionExit2D;

        private void OnTriggerEnter(Collider other) => _onTriggerEnter.Invoke();
        private void OnTriggerExit(Collider other) => _onTriggerExit.Invoke();
        private void OnCollisionEnter(Collision collision) => _onCollisionEnter.Invoke();
        private void OnCollisionExit(Collision collision) => _onCollisionExit.Invoke();
        private void OnTriggerEnter2D(Collider2D other) => _onTriggerEnter2D.Invoke();
        private void OnTriggerExit2D(Collider2D other) => _onTriggerExit2D.Invoke();
        private void OnCollisionEnter2D(Collision2D collision) => _onCollisionEnter2D.Invoke();
        private void OnCollisionExit2D(Collision2D collision) => _onCollisionExit2D.Invoke();
    }
}
