using System;
using System.IO;

using Microsoft.Extensions.Logging;

using ActivityTracker.Data;

namespace ActivityTracker.Services;

public sealed record TrackingStatusSnapshot(
    string CurrentAppName,
    DateTimeOffset? CurrentSegmentStart,
    DateTimeOffset? LastHeartbeatTime,
    double? LastGapSeconds,
    int OpenRowCount,
    int ConsecutiveWriteFailures,
    bool IsSuspended,
    long DatabaseFileSize,
    int DatabaseUserVersion);

public sealed class TrackingStatusService
{
    private readonly object _sync = new();
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<TrackingStatusService> _logger;

    private string _currentAppName = "";
    private DateTimeOffset? _currentSegmentStart;
    private DateTimeOffset? _lastHeartbeatTime;
    private double? _lastGapSeconds;
    private bool _isSuspended;

    public TrackingStatusService(
        SqliteConnectionFactory connectionFactory,
        ILogger<TrackingStatusService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public TrackingStatusSnapshot GetSnapshot()
    {
        string currentAppName;
        DateTimeOffset? currentSegmentStart;
        DateTimeOffset? lastHeartbeatTime;
        double? lastGapSeconds;
        bool isSuspended;

        lock (_sync)
        {
            currentAppName = _currentAppName;
            currentSegmentStart = _currentSegmentStart;
            lastHeartbeatTime = _lastHeartbeatTime;
            lastGapSeconds = _lastGapSeconds;
            isSuspended = _isSuspended;
        }

        var openRows = -1;
        var userVersion = -1;

        try
        {
            using var connection =
                _connectionFactory.OpenConnection();

            using var openCommand = connection.CreateCommand();
            openCommand.CommandText =
                SqlConstants.SelectTrackingOpenRowCount;
            openRows = Convert.ToInt32(openCommand.ExecuteScalar());

            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText =
                SqlConstants.GetUserVersion;
            userVersion = Convert.ToInt32(
                versionCommand.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "读取追踪运行状态失败。");
        }

        var databaseSize = File.Exists(
                _connectionFactory.DatabasePath)
            ? new FileInfo(
                    _connectionFactory.DatabasePath)
                .Length
            : 0;

        return new TrackingStatusSnapshot(
            currentAppName,
            currentSegmentStart,
            lastHeartbeatTime,
            lastGapSeconds,
            openRows,
            _connectionFactory.ConsecutiveWriteFailures,
            isSuspended,
            databaseSize,
            userVersion);
    }

    internal void UpdateCurrent(
        string appName,
        DateTimeOffset? segmentStart,
        bool isSuspended,
        double? lastGapSeconds)
    {
        lock (_sync)
        {
            _currentAppName = appName;
            _currentSegmentStart = segmentStart;
            _isSuspended = isSuspended;
            _lastGapSeconds = lastGapSeconds;
        }
    }

    internal void ReportHeartbeat(DateTimeOffset time)
    {
        lock (_sync)
            _lastHeartbeatTime = time;
    }
}
