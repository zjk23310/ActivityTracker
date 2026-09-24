using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services.Sinks;

public sealed class ActivityDatabaseSink : IHostedService, IDisposable
{
    private static readonly TimeSpan PendingDrainHardLimit =
        TimeSpan.FromSeconds(3);

    private readonly IActivityChangeBus _bus;
    private readonly ActivityRepository _repository;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TrackingStatusService _statusService;
    private readonly ILogger<ActivityDatabaseSink> _logger;
    private readonly Dictionary<string, long> _rowIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingWrite> _pendingWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _pendingWriteOrder = new();
    private IDisposable? _subscription;
    private int _stopped;

    public ActivityDatabaseSink(
        IActivityChangeBus bus,
        ActivityRepository repository,
        SqliteConnectionFactory connectionFactory,
        TrackingStatusService statusService,
        ILogger<ActivityDatabaseSink> logger)
    {
        _bus = bus;
        _repository = repository;
        _connectionFactory = connectionFactory;
        _statusService = statusService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(
            "Database",
            ActivitySubscriptionOptions.Database,
            HandleAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        var report = await _bus.DrainAsync(
            TimeSpan.FromSeconds(3),
            cancellationToken)
            .ConfigureAwait(false);

        foreach (var subscriber in report.Subscribers)
        {
            if (!subscriber.Drained)
            {
                _logger.LogError(
                    "活动事件订阅者未在限时内排空：Name={Name}，Pending={PendingCount}。",
                    subscriber.Name,
                    subscriber.PendingCount);
            }
        }

        await DrainPendingWritesAsync(PendingDrainHardLimit)
            .ConfigureAwait(false);

        var checkpointSucceeded = _connectionFactory.Checkpoint();
        var openRows = CountOpenRows();

        if (openRows > 0)
        {
            _logger.LogError(
                "数据库消费者停止后仍有未闭合会话：OpenRows={OpenRows}，PendingWrites={PendingWrites}，CheckpointSucceeded={CheckpointSucceeded}。",
                openRows,
                _pendingWrites.Count,
                checkpointSucceeded);
        }
        else
        {
            _logger.LogInformation(
                "数据库消费者停止校验通过：OpenRows=0，PendingWrites={PendingWrites}，CheckpointSucceeded={CheckpointSucceeded}。",
                _pendingWrites.Count,
                checkpointSucceeded);
        }
    }

    public ValueTask HandleAsync(
        ActivityChange change,
        CancellationToken cancellationToken)
    {
        RetryPendingWrites();
        PersistChange(change);
        return ValueTask.CompletedTask;
    }

    private void PersistChange(ActivityChange change)
    {
        var session = ToActivitySession(change);
        if (session.DurationSeconds <= 0 &&
            !_rowIds.ContainsKey(session.SessionKey))
        {
            return;
        }

        if (_rowIds.TryGetValue(session.SessionKey, out var rowId))
            session.Id = rowId;

        if (session.IsClosed)
            QueuePendingWrite(session);

        var result = _repository.SaveSegment(session);
        if (!result.Success)
        {
            QueuePendingWrite(session);
            return;
        }

        _rowIds[session.SessionKey] = result.Value;
        RemovePendingWrite(session.SessionKey, session);
        _statusService.ReportHeartbeat(change.EndLocal);
    }

    private void RetryPendingWrites()
    {
        var count = _pendingWrites.Count;
        for (var index = 0; index < count; index++)
        {
            var first = _pendingWriteOrder.First;
            if (first is null)
                break;

            var pending = _pendingWrites[first.Value];
            if (_rowIds.TryGetValue(first.Value, out var rowId))
                pending.Session.Id = rowId;

            var result = _repository.SaveSegment(pending.Session);
            if (!result.Success)
                break;

            _rowIds[first.Value] = result.Value;
            RemovePendingWrite(first.Value, pending.Session);
        }
    }

    private async Task DrainPendingWritesAsync(TimeSpan hardLimit)
    {
        var deadline = DateTime.UtcNow + hardLimit;
        while (_pendingWrites.Count > 0 && DateTime.UtcNow < deadline)
        {
            var before = _pendingWrites.Count;
            RetryPendingWrites();
            if (_pendingWrites.Count == 0)
                break;

            if (_pendingWrites.Count >= before)
                await Task.Delay(100).ConfigureAwait(false);
        }

        if (_pendingWrites.Count > 0)
        {
            _logger.LogError(
                "数据库待写队列达到 {HardLimitSeconds} 秒硬上限，仍有 {PendingWrites} 个会话快照。",
                hardLimit.TotalSeconds,
                _pendingWrites.Count);
        }
    }

    private void QueuePendingWrite(ActivitySession session)
    {
        if (_pendingWrites.TryGetValue(
                session.SessionKey,
                out var existing))
        {
            // 关闭快照具有终局性，任何更早到达的打开态快照都不能覆盖它。
            if (existing.Session.IsClosed && !session.IsClosed)
                return;

            existing.Session = session;
            return;
        }

        var node = _pendingWriteOrder.AddLast(session.SessionKey);
        _pendingWrites.Add(
            session.SessionKey,
            new PendingWrite(session, node));
    }

    private void RemovePendingWrite(
        string sessionKey,
        ActivitySession completedSnapshot)
    {
        if (!_pendingWrites.TryGetValue(
                sessionKey,
                out var pending) ||
            !ReferenceEquals(
                pending.Session,
                completedSnapshot))
        {
            return;
        }

        _pendingWrites.Remove(sessionKey);
        _pendingWriteOrder.Remove(pending.OrderNode);
    }

    private static ActivitySession ToActivitySession(ActivityChange change)
    {
        return new ActivitySession
        {
            ProcessName = change.Target.IsIdle
                ? "IDLE"
                : change.Target.ProcessName,
            WindowTitle = change.Target.IsIdle
                ? "用户空闲"
                : change.Target.WindowTitle,
            ExecutablePath = change.Target.IsIdle
                ? ""
                : change.Target.ExecutablePath,
            AppId = change.Target.AppId,
            AppName = change.Target.AppName,
            StartTime = change.StartLocal.LocalDateTime,
            EndTime = change.EndLocal.LocalDateTime,
            DurationSeconds = change.DurationSeconds,
            IsIdle = change.Target.IsIdle,
            SessionKey = change.SessionKey,
            StartUtcMs = change.StartUtcMs,
            EndUtcMs = change.EndUtcMs,
            LocalDate = change.LocalDate,
            IsClosed = change.Kind == ActivityChangeKind.Ended,
            CloseReason = change.Kind == ActivityChangeKind.Ended
                ? change.CloseReason
                : "",
            IsRecovered = false,
            Source = "live"
        };
    }

    private int CountOpenRows()
    {
        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SqlConstants.SelectTrackingOpenRowCount;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private sealed class PendingWrite
    {
        public PendingWrite(
            ActivitySession session,
            LinkedListNode<string> orderNode)
        {
            Session = session;
            OrderNode = orderNode;
        }

        public ActivitySession Session { get; set; }
        public LinkedListNode<string> OrderNode { get; }
    }
}
