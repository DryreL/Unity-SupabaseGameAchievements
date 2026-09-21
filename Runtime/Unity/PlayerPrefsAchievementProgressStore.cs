using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Keeps <see cref="AchievementRules"/> counters in <c>PlayerPrefs</c>: zero setup, but per device (not per
    /// account) and lost if the player clears the game's data. If your game has its own save system, use a
    /// <see cref="DelegateAchievementProgressStore"/> over it instead so counters travel with the save.
    /// </summary>
    public sealed class PlayerPrefsAchievementProgressStore : IFlushableAchievementProgressStore
    {
        private bool _dirty;

        public int Get(string key) => PlayerPrefs.GetInt(key, 0);

        public void Set(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
            _dirty = true; // writing to disk on every click would be wasteful; Flush runs on pause/quit
        }

        public void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            PlayerPrefs.Save();
        }
    }
}
