using System;
using System.IO;
using System.Text;

namespace DryreLHub.SupabaseGameAchievements.Editor
{
    /// <summary>Turns asset paths into the Resources paths the runtime loads by. Pure, so it is unit-tested.</summary>
    internal static class AchievementResourcePaths
    {
        /// <summary>
        /// <c>Assets/Resources/Achievements/sounds/unlock.wav</c> becomes <c>Achievements/sounds/unlock</c>: the part after
        /// the last <c>Resources/</c> folder, without the extension. Null when the asset is not inside a Resources folder
        /// (the game could not load it) or the path is empty.
        /// </summary>
        public static string FromAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return null;
            string path = assetPath.Replace('\\', '/');

            const string folder = "/Resources/";
            int at = path.LastIndexOf(folder, StringComparison.OrdinalIgnoreCase);
            string relative;
            if (at >= 0) relative = path.Substring(at + folder.Length);
            else if (path.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)) relative = path.Substring("Resources/".Length);
            else return null;

            int dot = relative.LastIndexOf('.');
            int slash = relative.LastIndexOf('/');
            if (dot > slash) relative = relative.Substring(0, dot);
            return relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal) ? null : relative;
        }
    }

    /// <summary>Which part of a toast a child object is meant to be, guessed from its name (used by "Auto-wire").</summary>
    internal enum ToastRole
    {
        None,
        Panel,
        Icon,
        Header,
        Title,
        Description,
    }

    internal static class AchievementToastRoles
    {
        /// <summary>
        /// "Title", "AchievementTitle" and "title_text" are the title; "Icon"/"Image" the icon; "Desc"/"Description"/"Subtitle" the
        /// description; "Header"/"Heading"/"Caption" the header line; "Panel"/"Background"/"Box" the panel. An icon's own
        /// background image is not an icon, and a name that fits nothing gives <see cref="ToastRole.None"/>.
        /// </summary>
        public static ToastRole FromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ToastRole.None;
            string n = name.ToLowerInvariant();

            if (Has(n, "iconbg") || Has(n, "iconback") || Has(n, "icon_back") || Has(n, "icon back") || Has(n, "icon_bg")) return ToastRole.None;
            if (Has(n, "header") || Has(n, "heading") || Has(n, "caption") || Has(n, "unlocked")) return ToastRole.Header;
            if (Has(n, "desc") || Has(n, "subtitle") || Has(n, "subtext") || Has(n, "detail")) return ToastRole.Description;
            if (Has(n, "title") || Has(n, "name")) return ToastRole.Title;
            if (Has(n, "icon")) return ToastRole.Icon;
            if (Has(n, "panel") || Has(n, "background") || Has(n, "box") || Has(n, "container") || Has(n, "frame")) return ToastRole.Panel;
            if (Has(n, "image") || Has(n, "sprite") || Has(n, "picture")) return ToastRole.Icon;
            return ToastRole.None;
        }

        private static bool Has(string haystack, string needle) => haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }

    /// <summary>Reading and writing the overlay settings file (LF line endings, UTF-8 without a BOM).</summary>
    internal static class AchievementOverlayFile
    {
        public const string DefaultPath = "Assets/Resources/Achievements/overlay.json";
        public const string DefaultPrefabPath = "Assets/Resources/Achievements/AchievementToast.prefab";
        public const string SoundFolder = "Assets/Resources/Achievements/sounds";
        public const string FontFolder = "Assets/Resources/Achievements/fonts";
        public const string PrefabFolder = "Assets/Resources/Achievements";

        /// <summary>Writes the file when its text would change. Returns true when it wrote.</summary>
        public static bool Write(string fullPath, string json)
        {
            json = json.Replace("\r\n", "\n");
            if (File.Exists(fullPath) && File.ReadAllText(fullPath).Replace("\r\n", "\n") == json) return false;

            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write beside the target and swap, so a crash never leaves a half-written file.
            string temp = fullPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(temp, fullPath);
            }
            catch (IOException)
            {
                File.WriteAllText(fullPath, json, new UTF8Encoding(false)); // the target was locked for the swap: write it directly
                File.Delete(temp);
            }
            return true;
        }
    }
}
