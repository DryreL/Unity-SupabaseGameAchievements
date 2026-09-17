using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DryreLHub.SupabaseGameAchievements.Unity
{
    /// <summary>
    /// Lets the packaged manifest be overridden by a value pushed through Unity Remote Config, without this
    /// package taking a hard dependency on either Unity Gaming Services or the legacy
    /// <c>com.unity.remote-config</c> package (reflection only — same technique this project already uses
    /// for Sentry's DSN and for Patreon promotion settings).
    /// </summary>
    /// <remarks>
    /// This class never starts a Remote Config fetch itself; that stays owned by whatever already
    /// initializes Remote Config in this project. It only:
    /// <list type="number">
    /// <item><description>Reads whatever value Remote Config already has right now (no network call, instant, safe to call every frame).</description></item>
    /// <item><description>Caches the last value that actually won to disk, so a cold start before the project's
    /// own fetch completes still gets the most recently known remote catalog instead of only the one bundled
    /// with the build.</description></item>
    /// <item><description>Never regresses: the bundled manifest, the disk cache and the live value are compared by
    /// <see cref="AchievementCatalog.CatalogVersion"/> for the same <see cref="AchievementCatalog.GameId"/>; the
    /// highest-versioned one that parses wins.</description></item>
    /// </list>
    /// The bundled manifest stays the required, always-present offline fallback — this is a purely additive,
    /// best-effort improvement on top of it, never a replacement for it.
    /// </remarks>
    public sealed class RemoteConfigAchievementCatalogSource
    {
        private readonly string _remoteConfigKey;
        private readonly string _cacheFilePath;
        private readonly IAchievementLogger _logger;
        private readonly Func<string, string> _liveValueReader;

        /// <param name="liveValueReader">
        /// Reads the current value for a Remote Config key, or null/empty if unavailable. Defaults to
        /// <see cref="RemoteConfigReflection.TryGetString"/>. Overridable for testing without a real Unity
        /// Remote Config package loaded, or to source the value from something else entirely.
        /// </param>
        public RemoteConfigAchievementCatalogSource(string remoteConfigKey, string cacheFilePath, IAchievementLogger logger = null, Func<string, string> liveValueReader = null)
        {
            if (string.IsNullOrEmpty(remoteConfigKey)) throw new ArgumentException("Remote Config key is required.", nameof(remoteConfigKey));
            _remoteConfigKey = remoteConfigKey;
            _cacheFilePath = cacheFilePath;
            _logger = logger ?? NullAchievementLogger.Instance;
            _liveValueReader = liveValueReader ?? RemoteConfigReflection.TryGetString;
        }

        /// <summary>
        /// Synchronous and offline-safe: picks the highest-<see cref="AchievementCatalog.CatalogVersion"/>
        /// catalog among <paramref name="bundled"/>, the on-disk cache, and whatever Remote Config already
        /// has fetched right now. Never throws; returns <paramref name="bundled"/> unchanged on any problem
        /// (missing/invalid Remote Config value, Remote Config not fetched yet, wrong game id, ...).
        /// </summary>
        public AchievementCatalog ResolveBest(AchievementCatalog bundled)
        {
            var best = bundled;

            if (TryReadCachedJson(out string cachedJson) && TryParse(cachedJson, bundled?.GameId, out var cached))
                best = Newer(best, cached);

            if (TryReadLiveJson(out string liveJson) && TryParse(liveJson, bundled?.GameId, out var live))
            {
                var winner = Newer(best, live);
                if (ReferenceEquals(winner, live)) WriteCache(liveJson); // only persist a value that actually won
                best = winner;
            }

            if (best != null && !ReferenceEquals(best, bundled))
                _logger.Info("Achievement catalog overridden by Remote Config ('" + _remoteConfigKey + "'), v" + best.CatalogVersion + ".");
            return best;
        }

        /// <summary>
        /// Call this once your project's own Remote Config fetch completes (the same completion hook already
        /// used elsewhere in this project, e.g. next to a Sentry DSN cache refresh). Returns true and the new
        /// catalog only when Remote Config now holds a strictly newer, valid catalog for the same game than
        /// <paramref name="current"/>; otherwise returns false and leaves the running catalog untouched.
        /// </summary>
        public bool TryRefresh(AchievementCatalog current, out AchievementCatalog refreshed)
        {
            refreshed = null;
            if (!TryReadLiveJson(out string json) || !TryParse(json, current?.GameId, out var candidate)) return false;
            if (current != null && candidate.CatalogVersion <= current.CatalogVersion) return false;

            WriteCache(json);
            refreshed = candidate;
            return true;
        }

        private static AchievementCatalog Newer(AchievementCatalog a, AchievementCatalog b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return b.CatalogVersion > a.CatalogVersion ? b : a;
        }

        private bool TryParse(string json, long? expectedGameId, out AchievementCatalog catalog)
        {
            catalog = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = AchievementCatalog.FromJson(json);
                if (expectedGameId.HasValue && parsed.GameId != expectedGameId.Value)
                {
                    _logger.Warning("Remote Config catalog ('" + _remoteConfigKey + "') is for game id " + parsed.GameId + ", expected " + expectedGameId.Value + "; ignoring it.");
                    return false;
                }
                catalog = parsed;
                return true;
            }
            catch (AchievementCatalogException e)
            {
                _logger.Warning("Remote Config catalog ('" + _remoteConfigKey + "') is invalid, ignoring it: " + e.Message);
                return false;
            }
        }

        private bool TryReadCachedJson(out string json)
        {
            json = null;
            try
            {
                if (string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath)) return false;
                json = File.ReadAllText(_cacheFilePath);
                return !string.IsNullOrWhiteSpace(json);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void WriteCache(string json)
        {
            if (string.IsNullOrEmpty(_cacheFilePath)) return;
            try
            {
                string directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string tempPath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        File.Replace(tempPath, _cacheFilePath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(_cacheFilePath);
                        File.Move(tempPath, _cacheFilePath);
                    }
                }
                else
                {
                    File.Move(tempPath, _cacheFilePath);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _logger.Warning("Could not cache the Remote Config achievement catalog: " + e.Message);
            }
        }

        private bool TryReadLiveJson(out string json)
        {
            json = _liveValueReader(_remoteConfigKey);
            return !string.IsNullOrEmpty(json);
        }
    }

    /// <summary>
    /// Reads a string value from whichever Remote Config service is present (Unity Gaming Services, or the
    /// legacy <c>com.unity.remote-config</c> package) via reflection only, so this package never needs a hard
    /// reference to either. Mirrors the reflection pattern already used elsewhere in this project for Remote
    /// Config (Sentry's DSN override, Patreon promotion settings) so it keeps working with whichever of the
    /// two the project has installed, with no compile-time dependency either way.
    /// </summary>
    internal static class RemoteConfigReflection
    {
        public static string TryGetString(string key)
        {
            try
            {
                var ugsType = Type.GetType("Unity.Services.RemoteConfig.RemoteConfigService, Unity.Services.RemoteConfig");
                if (ugsType != null)
                {
                    var instance = ugsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    var appConfig = instance != null
                        ? ugsType.GetProperty("appConfig", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
                        : null;
                    var value = GetValue(appConfig, key);
                    if (!string.IsNullOrEmpty(value)) return value;
                }

                var legacyType = Type.GetType("Unity.RemoteConfig.ConfigManager, Unity.RemoteConfig");
                if (legacyType != null)
                {
                    var appConfig = legacyType.GetProperty("appConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    var value = GetValue(appConfig, key);
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Achievements] Remote Config read failed: " + e.Message);
            }
            return null;
        }

        /// <summary>
        /// Reads <paramref name="key"/> as whichever value type it was configured with in the Remote Config
        /// dashboard. A key created with type "json" is read with <c>GetJson(key, "{}")</c> (the manifest
        /// pasted in as-is, unescaped); a key created with type "string" is read with <c>GetString(key, "")</c>
        /// (the manifest pasted in as one escaped string value). Both are tried, in that order, since the
        /// reflection here cannot see which type was actually chosen for the key.
        /// </summary>
        private static string GetValue(object appConfig, string key)
        {
            if (appConfig == null) return null;
            var type = appConfig.GetType();

            var getJson = type.GetMethod("GetJson", new[] { typeof(string), typeof(string) });
            var json = getJson?.Invoke(appConfig, new object[] { key, "{}" }) as string;
            if (!string.IsNullOrEmpty(json) && json != "{}") return json;

            var getString = type.GetMethod("GetString", new[] { typeof(string), typeof(string) });
            return getString?.Invoke(appConfig, new object[] { key, string.Empty }) as string;
        }
    }
}
