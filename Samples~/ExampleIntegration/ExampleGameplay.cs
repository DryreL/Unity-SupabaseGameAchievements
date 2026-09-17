using System.Collections.Generic;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Samples
{
    /// <summary>What gameplay code looks like. No networking, auth, storage or localization in sight.</summary>
    public sealed class ExampleGameplay : MonoBehaviour
    {
        private int _enemiesDefeated;

        private void OnEnable()
        {
            AchievementManager.AchievementUnlocked += OnAchievementUnlocked;
            AchievementManager.ServerStateMerged += OnServerStateMerged;
        }

        private void OnDisable()
        {
            AchievementManager.AchievementUnlocked -= OnAchievementUnlocked;
            AchievementManager.ServerStateMerged -= OnServerStateMerged;
        }

        public void OnEnemyDefeated()
        {
            _enemiesDefeated++;
            AchievementManager.TryUnlock("first_blood");          // cheap; returns false after the first time
            if (_enemiesDefeated >= 100) AchievementManager.TryUnlock("centurion");
        }

        public void OnLavaFieldCrossed() => AchievementManager.TryUnlock("lava_walker");

        public string DescribeProgress()
        {
            var definition = AchievementManager.GetDefinition("lava_walker");
            bool unlocked = AchievementManager.HasUnlocked("lava_walker");
            return definition == null ? "unknown" : definition.Title + (unlocked ? " ✓" : " (locked)");
        }

        private void OnAchievementUnlocked(AchievementUnlockedEvent unlocked)
        {
            // Fired once, the moment it is earned locally. The overlay is handled for you.
            Debug.Log("Unlocked " + unlocked.Definition.Key);
        }

        private void OnServerStateMerged(IReadOnlyList<AchievementDefinition> merged)
        {
            // Unlocks restored from another device or a reinstall: refresh achievement menus, never toast.
            Debug.Log("Restored " + merged.Count + " achievements from the server");
        }
    }
}

