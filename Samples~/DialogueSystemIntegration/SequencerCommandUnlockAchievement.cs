using DryreLHub.SupabaseGameAchievements;
using UnityEngine;

namespace PixelCrushers.DialogueSystem.SequencerCommands
{
    /// <summary>
    /// Dialogue System Sequence command: <c>UnlockAchievement(key)</c>
    /// <para>Usage: <c>UnlockAchievement(first_blood)</c>, optionally timed: <c>UnlockAchievement(first_blood)@2</c>.</para>
    /// Completes immediately; the unlock is local and never waits for the network. Repeated calls are no-ops.
    /// </summary>
    public class SequencerCommandUnlockAchievement : SequencerCommand
    {
        public void Awake()
        {
            string key = GetParameter(0);
            if (string.IsNullOrEmpty(key))
            {
                if (DialogueDebug.logWarnings) Debug.LogWarning("[Sequencer] UnlockAchievement() needs an achievement key, e.g. UnlockAchievement(first_blood).");
            }
            else
            {
                bool unlocked = AchievementManager.TryUnlock(key.Trim());
                if (DialogueDebug.logInfo) Debug.Log("[Sequencer] UnlockAchievement(" + key + ") -> " + (unlocked ? "unlocked" : "already unlocked or unknown"));
            }
            Stop();
        }
    }
}

