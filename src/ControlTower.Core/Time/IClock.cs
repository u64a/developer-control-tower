using System;

namespace ControlTower.Core.Time
{
    /// <summary>
    /// Minimal clock abstraction so time-sensitive composers can be unit-tested
    /// without coupling assertions to <see cref="DateTime.UtcNow"/>. Production
    /// code paths use <see cref="SystemClock.Instance"/>; tests supply a fixed
    /// instance to remove flakes.
    /// </summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        private SystemClock() { }

        public DateTime UtcNow => DateTime.UtcNow;
    }
}
