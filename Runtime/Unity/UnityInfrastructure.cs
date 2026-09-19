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
        /// <summary>
        /// Returns the best icon available right now, without waiting: packaged art, a previously
        /// cached network download, or a fallback. Must be fast and must never block on the network.
        /// Sets <paramref name="isFinal"/> to false when a better icon may still be on the way (a
        /// network fetch just started) — the caller can then also await <see cref="GetIconAsync"/> and
        /// swap the result in once it resolves, the same way achievement text is upgraded once
        /// localization finishes.
        /// </summary>
        Sprite GetIcon(AchievementDefinition definition, out bool isFinal);

        /// <summary>
        /// Resolves the definitive icon, awaiting a network fetch if one is in flight. Never throws: a
        /// failed fetch resolves to the same fallback <see cref="GetIcon"/> is already showing.
        /// </summary>
        Task<Sprite> GetIconAsync(AchievementDefinition definition, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Resolves one achievement's icon from packaged <c>Resources</c>, or downloads it when
    /// <c>IconPath</c> is a full URL — dual support, chosen per achievement by the value's shape, so a
    /// catalog can mix packaged art and hosted art freely. Never returns null and never leaves a blank
    /// gap: a missing or unloadable icon falls back, in order, to (1) the <c>fallback</c> sprite passed to
    /// the constructor, (2) a <c>fallback</c> sprite sitting next to the other icons in the same
    /// <c>Resources</c> folder (e.g. <c>Achievements/hellasure/fallback.png</c> for an achievement whose
    /// icon is <c>hellasure/lava_walker</c>), (3) <see cref="DefaultAchievementIcon"/>. This matters for an
    /// old, already-shipped build showing an achievement it did not ship art for (e.g. one added later
    /// purely through a Remote Config catalog update) — it still shows a clearly-intentional placeholder
    /// rather than a blank gap.
    /// </summary>
    public sealed class ResourcesAchievementIconProvider : IAchievementIconProvider
    {
        private sealed class IconEntry
        {
            public Sprite Sprite;
            public bool IsFinal;
            public Task<Sprite> PendingDownload;
            /// <summary>
            /// When set, called after <see cref="PendingDownload"/> resolves to null (download failed)
            /// to produce the next icon in the fallback chain (e.g. load from icon_path after an
            /// icon_url failure). Returns null to continue to <see cref="ResourcesAchievementIconProvider.ResolveFallback"/>.
            /// </summary>
            public Func<Task<Sprite>> DownloadFailureFallback;
        }

        private readonly string _prefix;
        private readonly Sprite _fallback;
        private readonly Func<string, Sprite> _localLoader;
        private readonly Func<string, Task<Sprite>> _urlDownloader;
        private readonly Dictionary<long, IconEntry> _cache = new Dictionary<long, IconEntry>();
        private readonly Dictionary<string, Sprite> _gameFallbackCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, Task<Sprite>> _urlDownloads = new Dictionary<string, Task<Sprite>>();

        /// <param name="fallback">Tried first for any icon that cannot be loaded. Null moves straight to the per-game "fallback" convention, then <see cref="DefaultAchievementIcon"/>.</param>
        /// <param name="localLoader">Loads a local Resources path. Defaults to <see cref="Resources.Load{T}(string)"/>; overridable for testing.</param>
        /// <param name="urlDownloader">Downloads an icon from a URL. Defaults to a real network fetch via <c>UnityWebRequestTexture</c>; overridable for testing.</param>
        public ResourcesAchievementIconProvider(
            string pathPrefix = "",
            Sprite fallback = null,
            Func<string, Sprite> localLoader = null,
            Func<string, Task<Sprite>> urlDownloader = null)
        {
            _prefix = pathPrefix ?? string.Empty;
            _fallback = fallback;
            _localLoader = localLoader ?? Resources.Load<Sprite>;
            _urlDownloader = urlDownloader ?? DownloadSpriteAsync;
        }

        public Sprite GetIcon(AchievementDefinition definition, out bool isFinal)
        {
            isFinal = true;
            if (definition == null) return ResolveFallback(null);

            if (_cache.TryGetValue(definition.Id, out var cached))
            {
                isFinal = cached.IsFinal;
                return cached.Sprite;
            }

            // --- Priority 1: icon_url (explicit remote URL column) ---
            string iconUrl = definition.IconUrl;
            if (!string.IsNullOrEmpty(iconUrl))
            {
                // Normalize www. prefix, identical to the icon_path URL path.
                if (!TryNormalizeUrl(iconUrl, out string normalizedUrl))
                    normalizedUrl = iconUrl; // already http/https; keep as-is
                var download = GetOrStartUrlDownload(normalizedUrl);
                var placeholder = ResolveFallback(definition);
                // If this download fails we want to retry the icon_path chain as the fallback.
                _cache[definition.Id] = new IconEntry
                {
                    Sprite = placeholder,
                    IsFinal = false,
                    PendingDownload = download,
                    DownloadFailureFallback = () => ResolveIconPathAsync(definition),
                };
                isFinal = false;
                return placeholder;
            }

            // --- Priority 2: icon_path (local Resources or URL-in-path, existing behaviour) ---
            string iconPath = definition.IconPath;
            if (iconPath != null && TryNormalizeUrl(iconPath, out string urlInPath))
            {
                var download = GetOrStartUrlDownload(urlInPath);
                var placeholder = ResolveFallback(definition);
                _cache[definition.Id] = new IconEntry { Sprite = placeholder, IsFinal = false, PendingDownload = download };
                isFinal = false;
                return placeholder;
            }

            Sprite sprite = LoadLocal(definition, iconPath);
            sprite = sprite != null ? sprite : ResolveFallback(definition);
            _cache[definition.Id] = new IconEntry { Sprite = sprite, IsFinal = true };
            return sprite;
        }

        public async Task<Sprite> GetIconAsync(AchievementDefinition definition, CancellationToken cancellationToken)
        {
            if (definition == null) return ResolveFallback(null);

            var sprite = GetIcon(definition, out bool isFinal);
            if (isFinal) return sprite;

            // Downloads are shared/cached by URL (GetOrStartUrlDownload) and never throw (failures
            // resolve to null). Await whichever request is already in flight for this icon.
            _cache.TryGetValue(definition.Id, out var entry);
            Sprite downloaded = entry?.PendingDownload != null ? await entry.PendingDownload : null;

            if (downloaded != null)
            {
                _cache[definition.Id] = new IconEntry { Sprite = downloaded, IsFinal = true };
                return downloaded;
            }

            // Download returned null (failed). Run the failure-fallback chain if one was registered
            // (i.e. icon_url failed → try icon_path), then fall through to ResolveFallback.
            if (entry?.DownloadFailureFallback != null)
            {
                Sprite fallbackSprite = null;
                try { fallbackSprite = await entry.DownloadFailureFallback(); }
                catch (Exception e) { Debug.LogWarning("[Achievements] Download failure-fallback threw for '" + definition.Key + "': " + e.Message); }

                if (fallbackSprite != null)
                {
                    _cache[definition.Id] = new IconEntry { Sprite = fallbackSprite, IsFinal = true };
                    return fallbackSprite;
                }
            }

            var resolved = ResolveFallback(definition);
            _cache[definition.Id] = new IconEntry { Sprite = resolved, IsFinal = true };
            return resolved;
        }

        /// <summary>
        /// Resolves the icon from <see cref="AchievementDefinition.IconPath"/> asynchronously,
        /// following the same local-load / URL-in-path / ResolveFallback chain as <see cref="GetIcon"/>.
        /// Used as the <see cref="IconEntry.DownloadFailureFallback"/> when an icon_url download fails.
        /// Returns null if the path is empty and there is no local file (caller will call ResolveFallback).
        /// </summary>
        private async Task<Sprite> ResolveIconPathAsync(AchievementDefinition definition)
        {
            string iconPath = definition.IconPath;
            if (string.IsNullOrEmpty(iconPath)) return null;

            if (TryNormalizeUrl(iconPath, out string url))
            {
                // icon_path itself is a URL: download it (shares the _urlDownloads cache).
                var urlDownload = GetOrStartUrlDownload(url);
                return await urlDownload;
            }

            // Local Resources load (synchronous, but wrapped here so the call site is uniform).
            return LoadLocal(definition, iconPath);
        }

        private Sprite LoadLocal(AchievementDefinition definition, string iconPath)
        {
            if (iconPath == null) return null;
            try
            {
                string path = _prefix + System.IO.Path.ChangeExtension(iconPath, null);
                var sprite = _localLoader(path);
                if (sprite == null)
                    Debug.LogWarning("[Achievements] Missing icon '" + path + "' for achievement '" + definition.Key +
                        "' (showing the fallback icon instead). Expected if this achievement was added after this " +
                        "build shipped, e.g. through a Remote Config catalog update, without matching packaged art.");
                return sprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Achievements] Failed to load the icon for achievement '" + definition.Key + "': " + e.Message +
                    " (showing the fallback icon instead).");
                return null;
            }
        }

        private Sprite ResolveFallback(AchievementDefinition definition)
        {
            if (_fallback != null) return _fallback;

            string gameFallbackPath = GameFallbackResourcePath(definition);
            if (gameFallbackPath != null)
            {
                if (!_gameFallbackCache.TryGetValue(gameFallbackPath, out var gameFallback))
                {
                    try
                    {
                        gameFallback = _localLoader(gameFallbackPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[Achievements] Failed to load the per-game fallback icon '" + gameFallbackPath + "': " + e.Message);
                        gameFallback = null;
                    }
                    _gameFallbackCache[gameFallbackPath] = gameFallback;
                }
                if (gameFallback != null) return gameFallback;
            }

            return DefaultAchievementIcon.GetOrCreate();
        }

        /// <summary>
        /// <c>&lt;prefix&gt;&lt;same folder as the achievement's own icon&gt;fallback</c> — e.g. an icon at
        /// <c>hellasure/lava_walker</c> looks for <c>&lt;prefix&gt;hellasure/fallback</c>. A remote-URL icon
        /// or an achievement with no icon path at all has no folder to derive this from and returns null.
        /// </summary>
        private string GameFallbackResourcePath(AchievementDefinition definition)
        {
            // Prefer the icon_url's implied folder (not meaningful for remote URLs), so derive
            // the fallback folder from icon_path which is the local Resources anchor.
            string iconPath = definition?.IconPath;
            if (string.IsNullOrEmpty(iconPath) || TryNormalizeUrl(iconPath, out _)) return null;
            int slash = iconPath.LastIndexOf('/');
            string directory = slash >= 0 ? iconPath.Substring(0, slash + 1) : string.Empty;
            return _prefix + directory + "fallback";
        }

        private static bool TryNormalizeUrl(string iconPath, out string url)
        {
            if (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = iconPath;
                return true;
            }
            if (iconPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + iconPath;
                return true;
            }
            url = null;
            return false;
        }

        private Task<Sprite> GetOrStartUrlDownload(string url)
        {
            if (_urlDownloads.TryGetValue(url, out var existing)) return existing;
            var download = _urlDownloader(url);
            _urlDownloads[url] = download;
            return download;
        }

        private static Task<Sprite> DownloadSpriteAsync(string url)
        {
            var completion = new TaskCompletionSource<Sprite>();
            try
            {
                var request = UnityWebRequestTexture.GetTexture(url);
                request.SendWebRequest().completed += _ =>
                {
                    try
                    {
                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            var texture = DownloadHandlerTexture.GetContent(request);
                            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                            completion.TrySetResult(sprite);
                        }
                        else
                        {
                            Debug.LogWarning("[Achievements] Failed to download icon '" + url + "': " + request.error);
                            completion.TrySetResult(null);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[Achievements] Failed to decode downloaded icon '" + url + "': " + e.Message);
                        completion.TrySetResult(null);
                    }
                    finally
                    {
                        request.Dispose();
                    }
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Achievements] Could not start icon download '" + url + "': " + e.Message);
                completion.TrySetResult(null);
            }
            return completion.Task;
        }
    }
}

