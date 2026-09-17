using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DryreLHub.SupabaseGameAchievements
{
    /// <summary>
    /// Presentation-only setting: whether unlock notifications are shown. It never affects tracking,
    /// persistence, or synchronization.
    /// </summary>
    public interface IAchievementNotificationSettingsProvider
    {
        /// <summary>Last known value. Cheap; no I/O.</summary>
        bool NotificationsEnabled { get; }

        /// <summary>Re-reads the underlying source if it changed. Keeps the last known value on any failure.</summary>
        void Refresh();
    }

    public sealed class FixedAchievementNotificationSettings : IAchievementNotificationSettingsProvider
    {
        public FixedAchievementNotificationSettings(bool enabled)
        {
            NotificationsEnabled = enabled;
        }

        public bool NotificationsEnabled { get; set; }

        public void Refresh() { }
    }

    /// <summary>
    /// Reads a shared JSON settings file written by the launcher (atomically, via temp file + rename).
    /// Tolerates a missing file, malformed or partially written JSON, older files without the key, and
    /// newer files with extra keys. The file is re-parsed only when its timestamp or size changes.
    /// </summary>
    /// <remarks>
    /// File shape (unknown keys are ignored):
    /// <code>{ "schema_version": 1, "achievement_notifications_enabled": true }</code>
    /// </remarks>
    public sealed class JsonFileAchievementNotificationSettings : IAchievementNotificationSettingsProvider
    {
        public const string EnabledKey = "achievement_notifications_enabled";
        private const long MaxFileBytes = 256 * 1024;

        private readonly string _path;
        private readonly IAchievementLogger _logger;
        private volatile bool _enabled;
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private long _lastLength = -1;

        public JsonFileAchievementNotificationSettings(string path, bool defaultEnabled = true, IAchievementLogger logger = null)
        {
            _path = path;
            _enabled = defaultEnabled;
            _logger = logger ?? NullAchievementLogger.Instance;
            Refresh();
        }

        public string Path => _path;

        public bool NotificationsEnabled => _enabled;

        public void Refresh()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length > MaxFileBytes) return; // keep last known value
                if (info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _lastLength) return;

                string json;
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                if (TryParse(json, out bool? enabled))
                {
                    // Only remember the timestamp for a parseable file, so a torn read is retried.
                    _lastWriteUtc = info.LastWriteTimeUtc;
                    _lastLength = info.Length;
                    if (enabled.HasValue) _enabled = enabled.Value;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _logger.Info("Launcher settings unavailable, keeping last known notification setting: " + e.Message);
            }
        }

        /// <summary>Returns false for unparseable JSON. <paramref name="enabled"/> is null if the key is absent or not a boolean.</summary>
        public static bool TryParse(string json, out bool? enabled)
        {
            enabled = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                if (!(JToken.Parse(json) is JObject root)) return false;
                var token = root[EnabledKey];
                if (token != null && token.Type == JTokenType.Boolean) enabled = (bool)token;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>Resolves the per-user configuration directory the launcher uses on each OS.</summary>
    public static class SharedSettingsLocation
    {
        /// <summary>
        /// Windows: %APPDATA%\&lt;vendor&gt;\&lt;file&gt;; macOS: ~/Library/Application Support/&lt;vendor&gt;/&lt;file&gt;;
        /// Linux: $XDG_CONFIG_HOME (or ~/.config)/&lt;vendor&gt;/&lt;file&gt;. Matches Rust's <c>dirs::config_dir()</c>.
        /// </summary>
        public static string Resolve(string vendorFolder, string fileName)
        {
            string root;
            string home = Environment.GetEnvironmentVariable("HOME");
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    break;
                case PlatformID.MacOSX:
                    root = System.IO.Path.Combine(home ?? string.Empty, "Library", "Application Support");
                    break;
                default:
                    // Mono reports macOS as Unix; distinguish by the presence of ~/Library/Application Support.
                    string macRoot = System.IO.Path.Combine(home ?? string.Empty, "Library", "Application Support");
                    if (!string.IsNullOrEmpty(home) && Directory.Exists(macRoot))
                    {
                        root = macRoot;
                    }
                    else
                    {
                        string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                        root = !string.IsNullOrEmpty(xdg) ? xdg : System.IO.Path.Combine(home ?? string.Empty, ".config");
                    }
                    break;
            }
            return System.IO.Path.Combine(root, vendorFolder, fileName);
        }
    }
}

