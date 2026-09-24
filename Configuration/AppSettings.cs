using System;

using Microsoft.Extensions.Logging;

namespace ActivityTracker.Configuration;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public TrackingSettings Tracking { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    internal void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Tracking ??= new TrackingSettings();
        Logging ??= new LoggingSettings();

        Tracking.IdleThresholdMinutes =
            Math.Clamp(Tracking.IdleThresholdMinutes, 1, 240);

        Tracking.CheckpointSeconds =
            Math.Clamp(Tracking.CheckpointSeconds, 5, 300);

        Tracking.GapThresholdSeconds =
            Math.Clamp(Tracking.GapThresholdSeconds, 10, 300);

        Tracking.MaxSessionMinutes =
            Math.Clamp(Tracking.MaxSessionMinutes, 30, 1440);

        Tracking.UiRefreshIntervalMs =
            Math.Clamp(Tracking.UiRefreshIntervalMs, 100, 5000);

        Logging.RetentionDays =
            Math.Clamp(Logging.RetentionDays, 1, 365);

        Logging.MaxFileSizeMb =
            Math.Clamp(Logging.MaxFileSizeMb, 1, 100);

        if (!Enum.TryParse<LogLevel>(
                Logging.MinimumLevel,
                ignoreCase: true,
                out var level) ||
            level == LogLevel.None)
        {
            Logging.MinimumLevel = nameof(LogLevel.Information);
        }
        else
        {
            Logging.MinimumLevel = level.ToString();
        }
    }
}

public sealed class TrackingSettings
{
    public int IdleThresholdMinutes { get; set; } = 5;
    public int CheckpointSeconds { get; set; } = 30;
    public int GapThresholdSeconds { get; set; } = 30;
    public int MaxSessionMinutes { get; set; } = 360;
    public int UiRefreshIntervalMs { get; set; } = 500;
    public bool SplitAtMidnight { get; set; } = true;
    public bool VerifyIntegrityOnStartup { get; set; }
}

public sealed class LoggingSettings
{
    public string MinimumLevel { get; set; } =
        nameof(LogLevel.Information);

    public int RetentionDays { get; set; } = 14;
    public int MaxFileSizeMb { get; set; } = 10;

    public LogLevel GetMinimumLevel()
    {
        return Enum.TryParse<LogLevel>(
            MinimumLevel,
            ignoreCase: true,
            out var level)
            ? level
            : LogLevel.Information;
    }
}
