using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Marshals work onto Unity's main thread through the player's own SynchronizationContext, so no
    /// extra pump object or thread is needed.
    /// </summary>
    public sealed class UnityMainThreadDispatcher : IAchievementDispatcher
    {
        public static readonly UnityMainThreadDispatcher Instance = new UnityMainThreadDispatcher();

        private static SynchronizationContext _context;
        private static int _mainThreadId = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Capture()
        {
            _context = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void Post(Action action)
        {
            if (action == null) return;
            if (IsMainThread || _context == null)
            {
                Invoke(action);
                return;
            }
            _context.Post(state => Invoke((Action)state), action);
        }

        private static void Invoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }

    public sealed class UnityAchievementLogger : IAchievementLogger
    {
        private const string Prefix = "[Achievements] ";

        public UnityAchievementLogger(bool verbose)
        {
            Verbose = verbose;
        }

        public bool Verbose { get; set; }

        public void Info(string message)
        {
            if (Verbose) Debug.Log(Prefix + message);
        }

        public void Warning(string message) => Debug.LogWarning(Prefix + message);

        public void Error(string message) => Debug.LogError(Prefix + message);
    }

    /// <summary>
    /// <see cref="IAchievementHttpTransport"/> on UnityWebRequest. Requests are created on the main thread
    /// (a Unity requirement) but the network I/O itself runs on Unity's native worker threads; completion is
    /// callback-driven, so nothing ever blocks a frame.
    /// </summary>
    public sealed class UnityWebRequestTransport : IAchievementHttpTransport
    {
        public Task<AchievementHttpResponse> PostJsonAsync(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<AchievementHttpResponse>();
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetResult(AchievementHttpResponse.NetworkError("cancelled"));
                return completion.Task;
            }
            UnityMainThreadDispatcher.Instance.Post(() => Send(url, jsonBody, headers, timeoutMilliseconds, cancellationToken, completion));
            return completion.Task;
        }

        private static void Send(
            string url,
            string jsonBody,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            TaskCompletionSource<AchievementHttpResponse> completion)
        {
            UnityWebRequest request;
            try
            {
                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody ?? string.Empty)),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = Math.Max(1, (timeoutMilliseconds + 999) / 1000),
                };
                request.SetRequestHeader("Content-Type", "application/json");
                if (headers != null)
                    foreach (var header in headers) request.SetRequestHeader(header.Key, header.Value);
            }
            catch (Exception e)
            {
                completion.TrySetResult(AchievementHttpResponse.NetworkError(e.Message));
                return;
            }

            var registration = cancellationToken.Register(() => UnityMainThreadDispatcher.Instance.Post(() =>
            {
                try { request.Abort(); } catch (Exception) { /* already disposed */ }
            }));

            request.SendWebRequest().completed += _ =>
            {
                try
                {
                    string body = request.downloadHandler != null ? request.downloadHandler.text : null;
                    switch (request.result)
                    {
                        case UnityWebRequest.Result.Success:
                        case UnityWebRequest.Result.ProtocolError:
                            completion.TrySetResult(new AchievementHttpResponse((int)request.responseCode, body));
                            break;
                        default:
                            completion.TrySetResult(AchievementHttpResponse.NetworkError(request.error));
                            break;
                    }
                }
                catch (Exception e)
                {
                    completion.TrySetResult(AchievementHttpResponse.NetworkError(e.Message));
                }
                finally
                {
                    registration.Dispose();
                    request.Dispose();
                }
            };
        }
    }

    /// <summary>
    /// Reads the launcher's shared settings file (see <see cref="JsonFileAchievementNotificationSettings"/>)
    /// from the OS-specific per-user config directory, re-checking it at most every few seconds.
    /// </summary>
    public sealed class UnityAchievementSettingsProvider : IAchievementNotificationSettingsProvider
    {
        private const float MinRefreshIntervalSeconds = 2f;

        private readonly JsonFileAchievementNotificationSettings _file;
        private float _lastRefresh = float.NegativeInfinity;

        public UnityAchievementSettingsProvider(string vendorFolder, string fileName, IAchievementLogger logger = null)
            : this(ResolvePath(vendorFolder, fileName), logger)
        {
        }

        public UnityAchievementSettingsProvider(string absolutePath, IAchievementLogger logger)
        {
            _file = new JsonFileAchievementNotificationSettings(absolutePath, true, logger);
            _lastRefresh = Time.realtimeSinceStartup;
        }

        public string Path => _file.Path;

        public bool NotificationsEnabled => _file.NotificationsEnabled;

        public void Refresh()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastRefresh < MinRefreshIntervalSeconds) return;
            _lastRefresh = now;
            _file.Refresh();
        }

        public static string ResolvePath(string vendorFolder, string fileName)
        {
            string home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), vendorFolder, fileName);
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return System.IO.Path.Combine(home, "Library", "Application Support", vendorFolder, fileName);
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                    return System.IO.Path.Combine(string.IsNullOrEmpty(xdg) ? System.IO.Path.Combine(home, ".config") : xdg, vendorFolder, fileName);
                default:
                    return SharedSettingsLocation.Resolve(vendorFolder, fileName);
            }
        }
    }

    /// <summary>Loads achievement icons packaged with the game.</summary>
    public interface IAchievementIconProvider
    {
        /// <summary>Returns the icon, or null if missing. Must be fast and offline.</summary>
        Sprite GetIcon(AchievementDefinition definition);
    }

    /// <summary>
    /// Loads <c>Resources/&lt;prefix&gt;&lt;IconPath&gt;</c> once per achievement and caches the result
    /// (including failures). Never returns null: a missing or unloadable icon falls back to
    /// <paramref name="fallback"/> passed to the constructor, or <see cref="DefaultAchievementIcon"/> if
    /// none was given. This matters for an old, already-shipped build showing an achievement it did not
    /// ship art for (e.g. one added later purely through a Remote Config catalog update) — it still shows a
    /// clearly-intentional placeholder rather than a blank gap.
    /// </summary>
    public sealed class ResourcesAchievementIconProvider : IAchievementIconProvider
    {
        private readonly string _prefix;
        private readonly Sprite _fallback;
        private readonly Dictionary<long, Sprite> _cache = new Dictionary<long, Sprite>();

        /// <param name="fallback">Used for any icon that cannot be loaded. Null uses <see cref="DefaultAchievementIcon"/>.</param>
        public ResourcesAchievementIconProvider(string pathPrefix = "", Sprite fallback = null)
        {
            _prefix = pathPrefix ?? string.Empty;
            _fallback = fallback;
        }

        public Sprite GetIcon(AchievementDefinition definition)
        {
            if (definition == null) return Fallback();
            if (_cache.TryGetValue(definition.Id, out var cached)) return cached;

            Sprite sprite = null;
            try
            {
                if (definition.IconPath != null)
                {
                    string path = _prefix + System.IO.Path.ChangeExtension(definition.IconPath, null);
                    sprite = Resources.Load<Sprite>(path);
                    if (sprite == null)
                        Debug.LogWarning("[Achievements] Missing icon '" + path + "' for achievement '" + definition.Key +
                            "' (showing the fallback icon instead). Expected if this achievement was added after this " +
                            "build shipped, e.g. through a Remote Config catalog update, without matching packaged art.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Achievements] Failed to load the icon for achievement '" + definition.Key + "': " + e.Message +
                    " (showing the fallback icon instead).");
                sprite = null;
            }

            sprite = sprite != null ? sprite : Fallback();
            _cache[definition.Id] = sprite;
            return sprite;
        }

        private Sprite Fallback() => _fallback != null ? _fallback : DefaultAchievementIcon.GetOrCreate();
    }
}

