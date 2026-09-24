using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ActivityTracker.Services;

public sealed record ActivitySubscriptionOptions(
    int Capacity,
    bool DropOldest,
    int WarningThreshold = 512)
{
    public static ActivitySubscriptionOptions Database { get; } =
        new(0, false);

    public static ActivitySubscriptionOptions LatestOnly { get; } =
        new(1, true);
}

public sealed record BusSubscriberDrainResult(
    string Name,
    int PendingCount,
    bool Drained);

public sealed record BusDrainReport(
    IReadOnlyList<BusSubscriberDrainResult> Subscribers)
{
    public bool AllDrained =>
        Subscribers.All(result => result.Drained);
}

public interface IActivityChangeBus
{
    BusDrainReport? LastDrainReport { get; }

    void Publish(ActivityChange change);

    IDisposable Subscribe(
        string name,
        ActivitySubscriptionOptions options,
        Func<ActivityChange, CancellationToken, ValueTask> handler);

    Task<BusDrainReport> DrainAsync(
        TimeSpan grace,
        CancellationToken cancellationToken);
}
