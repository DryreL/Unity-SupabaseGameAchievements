using System;
using System.Diagnostics;

namespace DryreLHub.SupabaseGameAchievements
{
    public interface IAchievementLogger
    {
        void Info(string message);

        void Warning(string message);

        void Error(string message);
    }

    public sealed class NullAchievementLogger : IAchievementLogger
    {
        public static readonly NullAchievementLogger Instance = new NullAchievementLogger();

        public void Info(string message) { }

        public void Warning(string message) { }

        public void Error(string message) { }
    }

    /// <summary>Time source. Injected so debounce/backoff/expiry logic is deterministic in tests.</summary>
    public interface IAchievementClock
    {
        /// <summary>Monotonic milliseconds (unaffected by wall-clock changes).</summary>
        long MonotonicMilliseconds { get; }

        DateTime UtcNow { get; }
    }

    public sealed class SystemAchievementClock : IAchievementClock
    {
        public static readonly SystemAchievementClock Instance = new SystemAchievementClock();

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long MonotonicMilliseconds => _stopwatch.ElapsedMilliseconds;

        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>
    /// Marshals callbacks to the thread that owns gameplay/UI state. Unity supplies a main-thread
    /// queue; the default runs callbacks inline.
    /// </summary>
    public interface IAchievementDispatcher
    {
        void Post(Action action);
    }

    public sealed class InlineAchievementDispatcher : IAchievementDispatcher
    {
        public static readonly InlineAchievementDispatcher Instance = new InlineAchievementDispatcher();

        public void Post(Action action) => action();
    }
}

