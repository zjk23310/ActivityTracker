using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ActivityTracker.Configuration;
using ActivityTracker.Models;
using ActivityTracker.Startup;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

public sealed class SessionTracker : IDisposable
{
    private static readonly TimeSpan StopCompletionHardLimit =
        TimeSpan.FromSeconds(3);

    private readonly IActivityChangeBus _changeBus;
    private readonly UiActivityNotifier _uiNotifier;
    private readonly AppIdentityResolver _appIdentityResolver;
    private readonly ForegroundWindowTracker _foregroundTracker;
    private readonly SettingsService _settingsService;
    private readonly IClock _clock;
    private readonly SessionSegmentManager _segmentManager;
    private readonly TrackingStatusService _statusService;
    private readonly PowerEventListener _powerEvents;
    private readonly ILogger<SessionTracker> _logger;
    private readonly ConcurrentQueue<TrackerRequest> _requests = new();
    private readonly object _managerSync = new();
    private readonly System.Threading.Timer _timer;

    private WindowInfo? _currentWindow;
    private AppIdentity? _currentIdentity;
    private int _workerScheduled;
    private int _started;
    private int _disposed;
    private bool _foregroundRefreshLogged;

    public SessionTracker(
        IActivityChangeBus changeBus,
        UiActivityNotifier uiNotifier,
        AppIdentityResolver appIdentityResolver,
        ForegroundWindowTracker foregroundTracker,
        SettingsService settingsService,
        IClock clock,
        SessionSegmentManager segmentManager,
        TrackingStatusService statusService,
        PowerEventListener powerEvents,
        ILogger<SessionTracker> logger)
    {
        _changeBus = changeBus;
        _uiNotifier = uiNotifier;
        _appIdentityResolver = appIdentityResolver;
        _foregroundTracker = foregroundTracker;
        _settingsService = settingsService;
        _clock = clock;
        _segmentManager = segmentManager;
        _statusService = statusService;
        _powerEvents = powerEvents;
        _logger = logger;
        _timer = new System.Threading.Timer(
            OnTimer,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _uiNotifier.SessionChanged += OnUiSessionChanged;
    }

    public event Action? SessionChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _foregroundTracker.ForegroundChanged +=
            OnForegroundChanged;
        _powerEvents.Suspended += OnSuspended;
        _powerEvents.Resumed += OnResumed;
        _powerEvents.SessionLocked += OnSessionLocked;
        _powerEvents.SessionUnlocked += OnSessionUnlocked;
        _powerEvents.SessionEnding += OnSessionEnding;

        _foregroundTracker.Start();
        _foregroundTracker.Refresh();
        _timer.Change(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));

        _logger.LogInformation(
            "活动追踪已启动，心跳 {CheckpointSeconds} 秒，断层阈值 {GapThresholdSeconds} 秒。",
            _settingsService.Current.Tracking.CheckpointSeconds,
            _settingsService.Current.Tracking.GapThresholdSeconds);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        _timer.Change(
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        Unsubscribe();

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new TrackerRequest(
            TrackerRequestKind.Shutdown,
            null,
            completion));

        var shutdownCompleted = false;
        try
        {
            await completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            shutdownCompleted = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "活动追踪停止被取消，继续在 {HardLimitSeconds} 秒硬上限内等待最后一段落库。",
                StopCompletionHardLimit.TotalSeconds);
        }

        if (!shutdownCompleted)
        {
            var completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(StopCompletionHardLimit))
                .ConfigureAwait(false);

            if (completed == completion.Task)
            {
                await completion.Task.ConfigureAwait(false);
            }
            else
            {
                _logger.LogError(
                    "活动追踪停止达到 {HardLimitSeconds} 秒硬上限，最后一段可能尚未落库。",
                    StopCompletionHardLimit.TotalSeconds);
            }
        }

        _foregroundTracker.Dispose();
        _logger.LogInformation("活动追踪已停止。");
    }

    public ActivitySession? GetCurrentSnapshot()
    {
        if (Volatile.Read(ref _started) == 0)
            return null;

        var nowLocal = _clock.LocalNow;
        var nowUtcMs = _clock.UtcNow.ToUnixTimeMilliseconds();
        var monotonic = _clock.MonotonicTimestamp;
        SessionSegmentAction? snapshot;

        lock (_managerSync)
        {
            snapshot = _segmentManager.GetSnapshot(
                nowLocal,
                nowUtcMs,
                monotonic);
        }

        return snapshot is null || snapshot.DurationSeconds <= 0
            ? null
            : ToActivitySession(snapshot, isClosed: false);
    }

    private void OnTimer(object? state)
    {
        if (Volatile.Read(ref _started) != 0)
            Enqueue(new TrackerRequest(TrackerRequestKind.Tick));
    }

    private void OnForegroundChanged(WindowInfo info)
    {
        if (Volatile.Read(ref _started) == 0)
            return;

        Enqueue(new TrackerRequest(
            TrackerRequestKind.ForegroundChanged,
            info));
    }

    private void OnSuspended() =>
        Enqueue(new TrackerRequest(TrackerRequestKind.Suspend));

    private void OnResumed() =>
        Enqueue(new TrackerRequest(TrackerRequestKind.Resume));

    private void OnSessionLocked() =>
        Enqueue(new TrackerRequest(TrackerRequestKind.Lock));

    private void OnSessionUnlocked() =>
        Enqueue(new TrackerRequest(TrackerRequestKind.Unlock));

    private void OnSessionEnding() =>
        Enqueue(new TrackerRequest(TrackerRequestKind.SessionEnding));

    private void Enqueue(TrackerRequest request)
    {
        _requests.Enqueue(request);

        if (Interlocked.Exchange(
                ref _workerScheduled,
                1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static tracker => tracker.RunWorker(),
                this,
                preferLocal: false);
        }
    }

    private void RunWorker()
    {
        try
        {
            while (_requests.TryDequeue(out var request))
            {
                try
                {
                    ProcessRequest(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "处理追踪请求 {RequestKind} 失败。",
                        request.Kind);
                    request.Completion?.TrySetResult(false);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _workerScheduled, 0);

            if (!_requests.IsEmpty &&
                Interlocked.Exchange(
                    ref _workerScheduled,
                    1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static tracker => tracker.RunWorker(),
                    this,
                    preferLocal: false);
            }
        }
    }

    private void ProcessRequest(TrackerRequest request)
    {
        switch (request.Kind)
        {
            case TrackerRequestKind.ForegroundChanged:
                ProcessForeground(request.Window!);
                break;
            case TrackerRequestKind.Tick:
                ProcessTick(notifyVisibleChange: false);
                break;
            case TrackerRequestKind.Suspend:
                ProcessSuspend(SessionCloseReasons.Suspend);
                break;
            case TrackerRequestKind.Lock:
                ProcessSuspend(SessionCloseReasons.Lock);
                break;
            case TrackerRequestKind.Resume:
            case TrackerRequestKind.Unlock:
                ProcessResume();
                break;
            case TrackerRequestKind.SessionEnding:
                ProcessSuspend(SessionCloseReasons.SessionEnding);
                break;
            case TrackerRequestKind.Shutdown:
                ProcessClose(SessionCloseReasons.Shutdown);
                request.Completion?.TrySetResult(true);
                break;
        }
    }

    private void ProcessForeground(WindowInfo info)
    {
        _currentWindow = info;
        _currentIdentity = _appIdentityResolver.Resolve(info);
        _foregroundRefreshLogged = false;
        ProcessTick(notifyVisibleChange: true);
    }

    private void ProcessTick(bool notifyVisibleChange)
    {
        var input = CreateInput();
        IReadOnlyList<SessionSegmentAction> actions;

        lock (_managerSync)
            actions = _segmentManager.ProcessTick(input);

        HandleActions(actions);
        UpdateStatus();
    }

    private void ProcessSuspend(string reason)
    {
        var nowLocal = _clock.LocalNow;
        var nowUtcMs = _clock.UtcNow.ToUnixTimeMilliseconds();
        SessionSegmentAction? action;

        lock (_managerSync)
        {
            action = _segmentManager.Suspend(
                reason,
                nowLocal,
                nowUtcMs,
                _clock.MonotonicTimestamp);
        }

        if (action is not null)
            PublishAction(action);

        UpdateStatus();
    }

    private void ProcessResume()
    {
        var settings = _settingsService.Current.Tracking;
        var isIdle = IdleDetector.GetIdleTime() >=
            TimeSpan.FromMinutes(settings.IdleThresholdMinutes);

        if (!isIdle)
        {
            lock (_managerSync)
                _segmentManager.ClearSuspended();

            _foregroundTracker.Refresh();
            UpdateStatus();
            return;
        }

        var input = CreateInput(forceIdle: true);
        IReadOnlyList<SessionSegmentAction> actions;
        lock (_managerSync)
            actions = _segmentManager.Resume(input);
        HandleActions(actions);
        UpdateStatus();
    }

    private void ProcessClose(string reason)
    {
        SessionSegmentAction? action;
        lock (_managerSync)
        {
            action = _segmentManager.Close(
                reason,
                _clock.LocalNow,
                _clock.UtcNow.ToUnixTimeMilliseconds(),
                _clock.MonotonicTimestamp);
        }

        if (action is not null)
            PublishAction(action);

        UpdateStatus();
    }

    private SessionTickInput CreateInput(bool? forceIdle = null)
    {
        var settings = _settingsService.Current.Tracking;
        var isIdle = forceIdle ??
            (IdleDetector.GetIdleTime() >=
             TimeSpan.FromMinutes(settings.IdleThresholdMinutes));
        var nowLocal = _clock.LocalNow;

        return new SessionTickInput(
            nowLocal,
            _clock.UtcNow.ToUnixTimeMilliseconds(),
            _clock.MonotonicTimestamp,
            _currentWindow,
            _currentIdentity,
            isIdle,
            settings);
    }

    private void HandleActions(
        IReadOnlyList<SessionSegmentAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case SessionSegmentActionKind.Open:
                    PublishAction(action);
                    break;
                case SessionSegmentActionKind.Heartbeat:
                    PublishAction(action);
                    break;
                case SessionSegmentActionKind.Close:
                    PublishAction(action);
                    LogCloseReason(action);
                    break;
                case SessionSegmentActionKind.RefreshForeground:
                    if (!_foregroundRefreshLogged)
                    {
                        _logger.LogInformation(
                            "连续读取不到前台窗口，开始按退避节奏重试。");
                        _foregroundRefreshLogged = true;
                    }
                    _foregroundTracker.Refresh();
                    break;
            }
        }

    }

    private void PublishAction(SessionSegmentAction action)
    {
        var window = action.Window;
        var isEnded = action.Kind == SessionSegmentActionKind.Close;
        var closeReason = isEnded
            ? (string.IsNullOrWhiteSpace(action.CloseReason)
                ? "Unknown"
                : action.CloseReason)
            : "";

        var change = new ActivityChange(
            action.Kind switch
            {
                SessionSegmentActionKind.Open => ActivityChangeKind.Started,
                SessionSegmentActionKind.Heartbeat => ActivityChangeKind.Updated,
                SessionSegmentActionKind.Close => ActivityChangeKind.Ended,
                _ => throw new InvalidOperationException(
                    "前台刷新动作不能发布为活动变更。")
            },
            action.SessionKey.ToString("D"),
            0,
            DateTimeOffset.FromUnixTimeMilliseconds(action.EndUtcMs),
            action.StartLocal,
            action.StartUtcMs,
            action.EndLocal,
            action.EndUtcMs,
            action.DurationSeconds,
            action.LocalDate,
            new ActivityTarget(
                action.Identity.AppId,
                action.Identity.AppName,
                action.IsIdle ? "IDLE" : window?.ProcessName ?? "",
                action.IsIdle ? "" : window?.ExecutablePath ?? "",
                action.IsIdle ? "用户空闲" : window?.WindowTitle ?? "",
                action.IsIdle ? 0 : window?.ProcessId ?? 0,
                action.IsIdle),
            closeReason);

        _changeBus.Publish(change);

        if (action.Kind == SessionSegmentActionKind.Heartbeat)
        {
            lock (_managerSync)
            {
                _segmentManager.MarkHeartbeatDispatched(
                    action.SessionKey,
                    action.EndLocal,
                    _clock.MonotonicTimestamp);
            }

            _statusService.ReportHeartbeat(action.EndLocal);
        }
    }

    private static ActivitySession ToActivitySession(
        SessionSegmentAction action,
        bool isClosed)
    {
        var window = action.Window;
        return new ActivitySession
        {
            Id = 0,
            ProcessName = action.IsIdle
                ? "IDLE"
                : window?.ProcessName ?? "",
            WindowTitle = action.IsIdle
                ? "用户空闲"
                : window?.WindowTitle ?? "",
            ExecutablePath = action.IsIdle
                ? ""
                : window?.ExecutablePath ?? "",
            AppId = action.Identity.AppId,
            AppName = action.Identity.AppName,
            StartTime = action.StartLocal.LocalDateTime,
            EndTime = action.EndLocal.LocalDateTime,
            DurationSeconds = action.DurationSeconds,
            IsIdle = action.IsIdle,
            SessionKey = action.SessionKey.ToString("D"),
            StartUtcMs = action.StartUtcMs,
            EndUtcMs = action.EndUtcMs,
            LocalDate = action.LocalDate,
            IsClosed = isClosed,
            CloseReason = isClosed ? action.CloseReason : "",
            IsRecovered = false,
            Source = "live"
        };
    }

    private void LogCloseReason(SessionSegmentAction action)
    {
        if (action.CloseReason == SessionCloseReasons.Gap)
        {
            _logger.LogWarning(
                "检测到心跳断层，间隔 {GapSeconds:F1} 秒；当前段已在最后一次 tick 处关闭。",
                _segmentManager.LastGapSeconds ?? 0);
        }
        else if (action.CloseReason == SessionCloseReasons.ClockChange)
        {
            _logger.LogWarning(
                "检测到系统时钟跳变，已按单调时钟关闭当前段（ClockChange）。");
        }
        else if (action.CloseReason == SessionCloseReasons.Midnight)
        {
            _logger.LogInformation("会话已在本地午夜切分。");
        }
    }

    private void UpdateStatus()
    {
        SessionSegmentAction? snapshot;
        lock (_managerSync)
        {
            snapshot = _segmentManager.GetSnapshot(
                _clock.LocalNow,
                _clock.UtcNow.ToUnixTimeMilliseconds(),
                _clock.MonotonicTimestamp);
        }

        _statusService.UpdateCurrent(
            snapshot?.Identity.AppName ?? "",
            snapshot?.StartLocal,
            _segmentManager.IsSuspended,
            _segmentManager.LastGapSeconds);
    }

    private void OnUiSessionChanged()
    {
        try
        {
            SessionChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "会话变化订阅者执行失败。");
        }
    }

    private void Unsubscribe()
    {
        _foregroundTracker.ForegroundChanged -= OnForegroundChanged;
        _powerEvents.Suspended -= OnSuspended;
        _powerEvents.Resumed -= OnResumed;
        _powerEvents.SessionLocked -= OnSessionLocked;
        _powerEvents.SessionUnlocked -= OnSessionUnlocked;
        _powerEvents.SessionEnding -= OnSessionEnding;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            StopAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            _timer.Dispose();
            _uiNotifier.SessionChanged -= OnUiSessionChanged;
            _foregroundTracker.Dispose();
        }
    }

    private enum TrackerRequestKind
    {
        Tick,
        ForegroundChanged,
        Suspend,
        Resume,
        Lock,
        Unlock,
        SessionEnding,
        Shutdown
    }

    private sealed record TrackerRequest(
        TrackerRequestKind Kind,
        WindowInfo? Window = null,
        TaskCompletionSource<bool>? Completion = null);

}
