using System;

namespace ActivityTracker.Services;

public enum ActivityChangeKind
{
    Started,
    Updated,
    Ended
}

public sealed record ActivityTarget(
    string AppId,
    string AppName,
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    int ProcessId,
    bool IsIdle);

public sealed record ActivityChange(
    ActivityChangeKind Kind,
    string SessionKey,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset StartLocal,
    long StartUtcMs,
    DateTimeOffset EndLocal,
    long EndUtcMs,
    int DurationSeconds,
    string LocalDate,
    ActivityTarget Target,
    string CloseReason);
