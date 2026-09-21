using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryreLHub.SupabaseGameAchievements.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    internal enum ToastCheckLevel { Ok, Warning, Error }

    internal enum ToastFix { None, AutoWire, RemoveRaycaster, MakeNonBlocking, AddCanvasGroup, MoveToResources }

    internal sealed class ToastCheck
    {
        public ToastCheckLevel Level;
        public string Message;
        public ToastFix Fix;
    }

    /// <summary>
    /// Helpers for the dashboard's "Build your own toast" page: builds a starter prefab, checks a prefab the user made
    /// against what <see cref="AchievementToastView"/> and the overlay need, and fixes what can be fixed automatically.
    /// </summary>
    internal static class AchievementToastPrefabTools
    {
        // The serialized fields of AchievementToastView / AchievementToastViewTMP, by property name.
        private const string PanelField = "_panel";
        private const string CanvasGroupField = "_canvasGroup";
        private const string IconField = "_icon";
        private const string HeaderField = "_header";
        private const string TitleField = "_title";
        private const string DescriptionField = "_description";
        private const string HeaderTmpField = "_headerTmp";
        private const string TitleTmpField = "_titleTmp";
        private const string DescriptionTmpField = "_descriptionTmp";

        // ---- checking -----------------------------------------------------------------------------------------

        public static List<ToastCheck> Check(GameObject prefab)
        {
            var checks = new List<ToastCheck>();
            if (prefab == null) return checks;

            var canvas = prefab.GetComponent<Canvas>();
            if (canvas == null)
                checks.Add(Error("The root object has no Canvas. The toast is created inside the game's manager object, which is not on a Canvas, so nothing would be drawn. Make the root a UI > Canvas."));
            else if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                checks.Add(Warning("The Canvas is not 'Screen Space - Overlay'. That works, but the toast then follows a camera or the world instead of sitting on the screen."));
            else
                checks.Add(Ok("Canvas: Screen Space - Overlay, Sort Order " + canvas.sortingOrder + (canvas.sortingOrder < 100 ? "  (raise it, e.g. 32000, so the toast draws above your game UI)" : "")));

            var raycaster = prefab.GetComponent<GraphicRaycaster>();
            if (raycaster != null)
                checks.Add(new ToastCheck { Level = ToastCheckLevel.Error, Fix = ToastFix.RemoveRaycaster, Message = "The Canvas has a Graphic Raycaster: the toast would eat the player's clicks. Remove it." });
            else
                checks.Add(Ok("No Graphic Raycaster, so the toast never blocks clicks."));

            var view = prefab.GetComponent<AchievementToastView>();
            if (view == null)
            {
                checks.Add(Error("The root object has no Achievement Toast View component. Add it with Add Component > DryreL Hub > Supabase Game Achievements > Achievement Toast View (use the TextMeshPro variant for TMP text)."));
                return checks;
            }
            checks.Add(Ok("Achievement Toast View found (" + view.GetType().Name + ")."));

            var so = new SerializedObject(view);
            var panel = ReferenceOf(so, PanelField) as RectTransform;
            var group = ReferenceOf(so, CanvasGroupField) as CanvasGroup;
            var icon = ReferenceOf(so, IconField);
            bool titleSet = ReferenceOf(so, TitleField) != null || ReferenceOf(so, TitleTmpField) != null;
            bool headerSet = ReferenceOf(so, HeaderField) != null || ReferenceOf(so, HeaderTmpField) != null;
            bool descriptionSet = ReferenceOf(so, DescriptionField) != null || ReferenceOf(so, DescriptionTmpField) != null;

            checks.Add(panel == null
                ? new ToastCheck { Level = ToastCheckLevel.Error, Fix = ToastFix.AutoWire, Message = "Panel is not assigned. This is the box that slides in and out; the overlay moves it and sets its screen corner." }
                : Ok("Panel: " + panel.name));

            if (panel != null && panel.anchorMin != panel.anchorMax)
                checks.Add(Warning("The Panel is stretched (its Min and Max anchors differ). The overlay anchors it to a corner, so give it a fixed Width and Height instead."));

            checks.Add(group == null
                ? new ToastCheck { Level = ToastCheckLevel.Warning, Fix = panel != null ? ToastFix.AddCanvasGroup : ToastFix.AutoWire, Message = "Canvas Group is not assigned, so the toast cannot fade. Add a Canvas Group to the Panel and assign it." }
                : Ok("Canvas Group: " + group.name));

            if (group != null && group.blocksRaycasts)
                checks.Add(new ToastCheck { Level = ToastCheckLevel.Warning, Fix = ToastFix.MakeNonBlocking, Message = "The Canvas Group has Blocks Raycasts on. Turn it off (and Interactable) so the toast never takes clicks." });

            checks.Add(titleSet
                ? Ok("Title text assigned.")
                : new ToastCheck { Level = ToastCheckLevel.Error, Fix = ToastFix.AutoWire, Message = "Title is not assigned: the achievement's name has nowhere to go." });
            checks.Add(descriptionSet
                ? Ok("Description text assigned.")
                : new ToastCheck { Level = ToastCheckLevel.Warning, Fix = ToastFix.AutoWire, Message = "Description is not assigned: the achievement's description will not be shown (fine if you do not want it)." });
            checks.Add(headerSet
                ? Ok("Header text assigned.")
                : new ToastCheck { Level = ToastCheckLevel.Warning, Fix = ToastFix.AutoWire, Message = "Header is not assigned: the 'ACHIEVEMENT UNLOCKED' line will not be shown (fine if you do not want it)." });
            checks.Add(icon != null
                ? Ok("Icon image assigned.")
                : new ToastCheck { Level = ToastCheckLevel.Warning, Fix = ToastFix.AutoWire, Message = "Icon is not assigned: the achievement's picture will not be shown (fine if you do not want it)." });

            if (view.GetType() == typeof(AchievementToastView) && TmpTextUnder(prefab) != null && !HasLegacyText(prefab))
                checks.Add(Warning("This prefab uses TextMeshPro text but the plain Achievement Toast View, which only knows the classic Text component. Swap it for 'Achievement Toast View (TextMeshPro)'."));

            string path = AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(path) && AchievementResourcePaths.FromAssetPath(path) == null)
                checks.Add(new ToastCheck { Level = ToastCheckLevel.Warning, Fix = ToastFix.MoveToResources, Message = "The prefab is not inside a Resources folder, so the built game cannot load it. Move it to " + AchievementOverlayFile.PrefabFolder + "/." });

            return checks;
        }

        // ---- fixing -------------------------------------------------------------------------------------------

        /// <summary>Applies one automatic fix to the prefab asset. Returns a message for the user.</summary>
        public static string ApplyFix(string prefabPath, ToastFix fix)
        {
            if (string.IsNullOrEmpty(prefabPath)) return "Choose a prefab first.";
            if (fix == ToastFix.MoveToResources) return MoveToResources(prefabPath);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                string message;
                switch (fix)
                {
                    case ToastFix.RemoveRaycaster:
                        foreach (var raycaster in root.GetComponents<GraphicRaycaster>()) UnityEngine.Object.DestroyImmediate(raycaster);
                        message = "Removed the Graphic Raycaster.";
                        break;
                    case ToastFix.MakeNonBlocking:
                        foreach (var group in root.GetComponentsInChildren<CanvasGroup>(true))
                        {
                            group.blocksRaycasts = false;
                            group.interactable = false;
                        }
                        message = "The Canvas Group no longer blocks clicks.";
                        break;
                    case ToastFix.AddCanvasGroup:
                        message = AddCanvasGroup(root);
                        break;
                    case ToastFix.AutoWire:
                        message = AutoWire(root);
                        break;
                    default:
                        return "Nothing to fix.";
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return message;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string MoveToResources(string prefabPath)
        {
            EnsureFolder(AchievementOverlayFile.PrefabFolder);
            string target = AssetDatabase.GenerateUniqueAssetPath(AchievementOverlayFile.PrefabFolder + "/" + Path.GetFileName(prefabPath));
            string error = AssetDatabase.MoveAsset(prefabPath, target);
            return string.IsNullOrEmpty(error) ? "Moved the prefab to " + target + "." : "Could not move the prefab: " + error;
        }

        private static string AddCanvasGroup(GameObject root)
        {
            var view = root.GetComponent<AchievementToastView>();
            if (view == null) return "Add the Achievement Toast View first.";
            var so = new SerializedObject(view);
            var panel = ReferenceOf(so, PanelField) as RectTransform;
            if (panel == null) return "Assign the Panel first.";

            var group = panel.GetComponent<CanvasGroup>() ?? panel.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            Assign(so, CanvasGroupField, group);
            so.ApplyModifiedPropertiesWithoutUndo();
            return "Added a Canvas Group to the Panel and assigned it.";
        }

        /// <summary>
        /// Fills every empty field by looking at child object names (Panel, Icon, Header, Title, Description) and
        /// picking the classic Text / TextMeshPro / Image component on them. Fields already assigned are left alone.
        /// </summary>
        public static string AutoWire(GameObject root)
        {
            var view = root.GetComponent<AchievementToastView>();
            if (view == null) return "Add the Achievement Toast View component to the root object first.";

            var so = new SerializedObject(view);
            var found = new List<string>();

            if (ReferenceOf(so, PanelField) == null)
            {
                var panel = FindByRole(root.transform, ToastRole.Panel) as RectTransform ?? FirstChildRect(root.transform);
                if (panel != null && Assign(so, PanelField, panel)) found.Add("Panel = " + panel.name);
            }

            var panelRect = ReferenceOf(so, PanelField) as RectTransform;
            if (ReferenceOf(so, CanvasGroupField) == null && panelRect != null)
            {
                var group = panelRect.GetComponent<CanvasGroup>() ?? panelRect.gameObject.AddComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;
                if (Assign(so, CanvasGroupField, group)) found.Add("Canvas Group on " + panelRect.name);
            }

            if (ReferenceOf(so, IconField) == null)
            {
                var image = FindComponentByRole<Image>(root.transform, ToastRole.Icon);
                if (image != null && Assign(so, IconField, image)) found.Add("Icon = " + image.name);
            }

            found.AddRange(WireText(so, root.transform, ToastRole.Header, HeaderField, HeaderTmpField));
            found.AddRange(WireText(so, root.transform, ToastRole.Title, TitleField, TitleTmpField));
            found.AddRange(WireText(so, root.transform, ToastRole.Description, DescriptionField, DescriptionTmpField));

            so.ApplyModifiedPropertiesWithoutUndo();
            return found.Count == 0
                ? "Nothing new to connect. Name your child objects Panel, Icon, Header, Title and Description and try again."
                : "Connected: " + string.Join(", ", found) + ".";
        }

        private static IEnumerable<string> WireText(SerializedObject so, Transform root, ToastRole role, string legacyField, string tmpField)
        {
            var legacy = so.FindProperty(legacyField);
            if (legacy != null && legacy.objectReferenceValue == null)
            {
                var text = FindComponentByRole<Text>(root, role);
                if (text != null && Assign(so, legacyField, text)) yield return role + " = " + text.name;
            }

            var tmp = so.FindProperty(tmpField);
            if (tmp != null && tmp.objectReferenceValue == null)
            {
                var component = FindTmpByRole(root, role);
                if (component != null && Assign(so, tmpField, component)) yield return role + " (TMP) = " + component.name;
            }
        }

        // ---- the starter prefab -------------------------------------------------------------------------------

        /// <summary>
        /// Builds a complete, working toast (Canvas, Panel with a Canvas Group, Icon, Header, Title, Description, and the
        /// view with everything connected) and saves it as a prefab. Returns the prefab path.
        /// </summary>
        public static string CreateStarterPrefab(string prefabPath)
        {
            EnsureFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));
            prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabPath);

            var root = new GameObject("AchievementToast", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            try
            {
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;

                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                var panel = NewRect("Panel", root.transform, new Vector2(1, 0), new Vector2(440, 108));
                panel.pivot = new Vector2(1, 0);
                var background = panel.gameObject.AddComponent<Image>();
                background.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
                background.type = Image.Type.Sliced;
                background.color = new Color(0.08f, 0.07f, 0.11f, 0.96f);
                background.raycastTarget = false;
                var group = panel.gameObject.AddComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;

                var icon = NewRect("Icon", panel, new Vector2(0, 0.5f), new Vector2(72, 72));
                icon.pivot = new Vector2(0, 0.5f);
                icon.anchoredPosition = new Vector2(20, 0);
                var iconImage = icon.gameObject.AddComponent<Image>();
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
                // no sprite yet: the game assigns each achievement's icon at runtime

                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                var header = NewText("Header", panel, font, "ACHIEVEMENT UNLOCKED", 16, FontStyle.Bold, new Color(1f, 0.83f, 0.35f), 10, 20);
                var title = NewText("Title", panel, font, "Achievement title", 24, FontStyle.Bold, Color.white, 30, 32);
                var description = NewText("Description", panel, font, "What the player did to earn it.", 18, FontStyle.Normal, new Color(0.82f, 0.82f, 0.88f), 64, 36);

                var view = root.AddComponent<AchievementToastView>();
                var so = new SerializedObject(view);
                Assign(so, PanelField, panel);
                Assign(so, CanvasGroupField, group);
                Assign(so, IconField, iconImage);
                Assign(so, HeaderField, header);
                Assign(so, TitleField, title);
                Assign(so, DescriptionField, description);
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.ImportAsset(prefabPath);
            return prefabPath;
        }

        private static RectTransform NewRect(string name, Transform parent, Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.sizeDelta = size;
            return rect;
        }

        private static Text NewText(string name, RectTransform parent, Font font, string content, int size, FontStyle style, Color color, float top, float height)
        {
            var rect = NewRect(name, parent, new Vector2(0, 1), new Vector2(316, height));
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(108, -top);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.text = content;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = Mathf.Max(1, size - 6);
            text.resizeTextMaxSize = size;
            text.raycastTarget = false;
            return text;
        }

        // ---- small helpers ------------------------------------------------------------------------------------

        private static ToastCheck Ok(string message) => new ToastCheck { Level = ToastCheckLevel.Ok, Message = message };
        private static ToastCheck Warning(string message) => new ToastCheck { Level = ToastCheckLevel.Warning, Message = message };
        private static ToastCheck Error(string message) => new ToastCheck { Level = ToastCheckLevel.Error, Message = message };

        private static UnityEngine.Object ReferenceOf(SerializedObject so, string field) => so.FindProperty(field)?.objectReferenceValue;

        private static bool Assign(SerializedObject so, string field, UnityEngine.Object value)
        {
            var property = so.FindProperty(field);
            if (property == null || value == null) return false;
            property.objectReferenceValue = value;
            return true;
        }

        private static Transform FindByRole(Transform root, ToastRole role) =>
            root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != root && AchievementToastRoles.FromName(t.name) == role);

        private static RectTransform FirstChildRect(Transform root) => root.childCount > 0 ? root.GetChild(0) as RectTransform : null;

        private static T FindComponentByRole<T>(Transform root, ToastRole role) where T : Component =>
            root.GetComponentsInChildren<T>(true).FirstOrDefault(c => AchievementToastRoles.FromName(c.name) == role);

        private static readonly Type TmpTextType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");

        private static Component FindTmpByRole(Transform root, ToastRole role)
        {
            if (TmpTextType == null) return null;
            return root.GetComponentsInChildren(TmpTextType, true).FirstOrDefault(c => AchievementToastRoles.FromName(c.name) == role);
        }

        private static Component TmpTextUnder(GameObject root) =>
            TmpTextType == null ? null : root.GetComponentsInChildren(TmpTextType, true).FirstOrDefault();

        private static bool HasLegacyText(GameObject root) => root.GetComponentInChildren<Text>(true) != null;

        /// <summary>Creates a folder (and its parents) inside Assets.</summary>
        internal static void EnsureFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder)) return;

            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || parent == assetFolder) return;
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }
    }
}
