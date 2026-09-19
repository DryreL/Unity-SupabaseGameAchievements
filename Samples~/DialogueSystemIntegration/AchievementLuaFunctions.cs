using DryreLHub.SupabaseGameAchievements;
using PixelCrushers.DialogueSystem;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Integrations
{
    /// <summary>
    /// Exposes achievements to Dialogue System for Unity Lua. Add this component to the Dialogue Manager.
    /// <list type="bullet">
    /// <item><description>Script field: <c>UnlockAchievement("first_blood")</c></description></item>
    /// <item><description>Conditions field: <c>HasAchievement("first_blood")</c> or <c>HasAchievement("first_blood") == false</c></description></item>
    /// </list>
    /// Unlocking is idempotent, so re-running a node (loading a save, replaying a conversation) never
    /// shows the notification twice.
    /// </summary>
    [AddComponentMenu("Achievements/Achievement Lua Functions")]
    public sealed class AchievementLuaFunctions : MonoBehaviour
    {
        [Tooltip("Unregister the Lua functions when this component is disabled. Turn off to keep them available while the Dialogue Manager object is toggled.")]
        [SerializeField] private bool _unregisterOnDisable = true;

        private void OnEnable()
        {
            Lua.RegisterFunction(nameof(UnlockAchievement), this, SymbolExtensions.GetMethodInfo(() => UnlockAchievement(string.Empty)));
            Lua.RegisterFunction(nameof(HasAchievement), this, SymbolExtensions.GetMethodInfo(() => HasAchievement(string.Empty)));
        }

        private void OnDisable()
        {
            if (!_unregisterOnDisable) return;
            Lua.UnregisterFunction(nameof(UnlockAchievement));
            Lua.UnregisterFunction(nameof(HasAchievement));
        }

        /// <summary>Lua: <c>UnlockAchievement("key")</c>. Returns true only on the first unlock.</summary>
        public bool UnlockAchievement(string achievementKey)
        {
            if (string.IsNullOrEmpty(achievementKey))
            {
                Debug.LogWarning("[Achievements] UnlockAchievement called from Lua without a key.");
                return false;
            }
            return AchievementManager.TryUnlock(achievementKey.Trim().Trim('"', '\''));
        }

        /// <summary>Lua: <c>HasAchievement("key")</c>.</summary>
        public bool HasAchievement(string achievementKey) =>
            !string.IsNullOrEmpty(achievementKey) && AchievementManager.HasUnlocked(achievementKey.Trim().Trim('"', '\''));
    }
}

