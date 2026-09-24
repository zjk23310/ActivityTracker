using System;
using System.Diagnostics;

namespace ActivityTracker.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset LocalNow { get; }
    long MonotonicTimestamp { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset LocalNow => DateTimeOffset.Now;

    public long MonotonicTimestamp => Stopwatch.GetTimestamp();
}
