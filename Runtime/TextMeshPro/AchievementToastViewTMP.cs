using TMPro;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// <see cref="AchievementToastView"/> for toasts built with TextMeshPro. Put this on the root of your toast prefab
    /// instead of the plain view, assign Panel / Canvas Group / Icon as usual, and drop your TextMeshPro objects into the
    /// three TMP fields. Compiled only when TextMeshPro is available (it ships inside <c>com.unity.ugui</c> from Unity 6).
    /// </summary>
    [AddComponentMenu("DryreL Hub/Supabase Game Achievements/Achievement Toast View (TextMeshPro)")]
    public class AchievementToastViewTMP : AchievementToastView
    {
        [Tooltip("Optional. The 'ACHIEVEMENT UNLOCKED' line.")]
        [SerializeField] private TMP_Text _headerTmp;
        [SerializeField] private TMP_Text _titleTmp;
        [Tooltip("Optional.")]
        [SerializeField] private TMP_Text _descriptionTmp;

        public override void SetContent(string header, string title, string description, Sprite icon)
        {
            base.SetContent(header, title, description, icon);
            if (_headerTmp != null) _headerTmp.text = header;
            if (_titleTmp != null) _titleTmp.text = title;
            if (_descriptionTmp != null) _descriptionTmp.text = description;
        }

        public override void SetText(string title, string description)
        {
            base.SetText(title, description);
            if (_titleTmp != null) _titleTmp.text = title;
            if (_descriptionTmp != null) _descriptionTmp.text = description;
        }

        public override string TitleText => _titleTmp != null ? _titleTmp.text : base.TitleText;

        public override string DescriptionText => _descriptionTmp != null ? _descriptionTmp.text : base.DescriptionText;
    }
}
