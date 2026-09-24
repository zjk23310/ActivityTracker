using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using ActivityTracker.Configuration;
using ActivityTracker.Models;

namespace ActivityTracker.Services;

public enum SessionSegmentActionKind
{
    Open,
    Heartbeat,
    Close,
    RefreshForeground
}

public static class SessionCloseReasons
{
    public const string Gap = "Gap";
    public const string ClockChange = "ClockChange";
    public const string Midnight = "Midnight";
    public const string Idle = "Idle";
    public const string Resume = "Resume";
    public const string WindowChanged = "WindowChanged";
    public const string Suspend = "Suspend";
    public const string Lock = "Lock";
    public const string SessionEnding = "SessionEnding";
    public const string Shutdown = "Shutdown";
}

public sealed record SessionTickInput(
    DateTimeOffset NowLocal,
    long NowUtcMs,
    long MonotonicTimestamp,
    WindowInfo? Window,
    AppIdentity? Identity,
    bool IsIdle,
    TrackingSettings Settings);

public sealed record SessionSegmentAction(
    SessionSegmentActionKind Kind,
    Guid SessionKey,
    long? PersistedRowId,
    DateTimeOffset StartLocal,
    long StartUtcMs,
    DateTimeOffset EndLocal,
    long EndUtcMs,
    int DurationSeconds,
    string LocalDate,
    WindowInfo? Window,
    AppIdentity Identity,
    bool IsIdle,
    string CloseReason);

// 纯会话状态机：不持有 Timer、不访问数据库，也不调用 Windows API。
// 测试只需输入墙上时钟、UTC 毫秒和单调时钟即可覆盖所有边界。
public sealed class SessionSegmentManager
{
    // 挂起后若 tick 仍按正常节奏到达且持续存在输入，说明挂起广播可能被否决，
    // 或恢复广播丢失。约 30 秒后自动解除挂起，避免追踪永久停摆。
    private const double SuspendedTickToleranceSeconds = 5;
    private const int SuspendedActiveTickThreshold = 15;
    private const int ForegroundRefreshRetryInterval = 5;

    private SegmentState? _segment;
    private DateTimeOffset? _lastTickLocal;
    private long _lastTickUtcMs;
    private long? _lastTickMonotonic;
    private bool _isSuspended;
    private int _suspendedTickCount;
    private int _foregroundRefreshMissCount;

    public bool IsSuspended => _isSuspended;

    public double? LastGapSeconds { get; private set; }

    public IReadOnlyList<SessionSegmentAction> ProcessTick(
        SessionTickInput input)
    {
        var actions = new List<SessionSegmentAction>();

        if (_isSuspended)
        {
            if (_lastTickMonotonic.HasValue &&
                SecondsBetween(
                    _lastTickMonotonic.Value,
                    input.MonotonicTimestamp) <=
                    SuspendedTickToleranceSeconds &&
                !input.IsIdle)
            {
                _suspendedTickCount++;
            }
            else
            {
                _suspendedTickCount = 0;
            }

            RememberTick(input);

            if (_suspendedTickCount <
                SuspendedActiveTickThreshold)
            {
                return actions;
            }

            _isSuspended = false;
            _suspendedTickCount = 0;
            // 本次 tick 继续走正常流程，效果等同收到一次 Resume。
        }

        if (_segment is null)
        {
            if (!input.IsIdle &&
                (input.Window is null || input.Identity is null))
            {
                _foregroundRefreshMissCount++;
                if (_foregroundRefreshMissCount %
                    ForegroundRefreshRetryInterval == 0)
                {
                    actions.Add(CreateRefreshAction(input));
                }
            }
            else
            {
                _foregroundRefreshMissCount = 0;
                TryOpen(input, actions);
            }
            RememberTick(input);
            return actions;
        }

        var monotonicElapsed = SecondsBetween(
            _segment.MonotonicStart,
            input.MonotonicTimestamp);
        var wallElapsed =
            (input.NowLocal - _segment.StartLocal).TotalSeconds;

        if (input.IsIdle ||
            (input.Window is not null && input.Identity is not null))
        {
            _foregroundRefreshMissCount = 0;
        }

        if (input.NowLocal < _segment.StartLocal ||
            Math.Abs(wallElapsed - monotonicElapsed) > 5)
        {
            var safeEndLocal =
                _segment.StartLocal.AddSeconds(monotonicElapsed);
            var safeEndUtcMs =
                _segment.StartUtcMs +
                (long)Math.Round(monotonicElapsed * 1000);

            actions.Add(CloseCurrent(
                SessionCloseReasons.ClockChange,
                safeEndLocal,
                safeEndUtcMs,
                input.MonotonicTimestamp));

            TryOpen(input, actions);
            RememberTick(input);
            return actions;
        }

        if (_lastTickMonotonic.HasValue)
        {
            var gapSeconds = SecondsBetween(
                _lastTickMonotonic.Value,
                input.MonotonicTimestamp);

            if (gapSeconds > input.Settings.GapThresholdSeconds)
            {
                LastGapSeconds = gapSeconds;

                actions.Add(CloseCurrent(
                    SessionCloseReasons.Gap,
                    _lastTickLocal ?? _segment.StartLocal,
                    _lastTickUtcMs,
                    _lastTickMonotonic.Value));

                if (input.IsIdle)
                {
                    TryOpen(input, actions);
                }
                else
                {
                    actions.Add(CreateRefreshAction(input));
                }

                RememberTick(input);
                return actions;
            }
        }

        if (input.Settings.SplitAtMidnight &&
            input.NowLocal >= _segment.NextMidnightLocal)
        {
            var oldWindow = _segment.Window;
            var oldIdentity = _segment.Identity;
            var oldIsIdle = _segment.IsIdle;
            var midnight = _segment.NextMidnightLocal;
            var midnightUtcMs =
                midnight.ToUniversalTime().ToUnixTimeMilliseconds();
            var millisecondsAfterMidnight =
                Math.Max(
                    0,
                    (input.NowLocal - midnight).TotalMilliseconds);
            var midnightMonotonic =
                input.MonotonicTimestamp -
                ToMonotonicTicks(millisecondsAfterMidnight / 1000d);

            actions.Add(CloseCurrent(
                SessionCloseReasons.Midnight,
                midnight.AddMilliseconds(-1),
                midnightUtcMs - 1,
                midnightMonotonic));

            OpenAt(
                midnight,
                midnightUtcMs,
                midnightMonotonic,
                oldWindow,
                oldIdentity,
                oldIsIdle,
                actions);

            RememberTick(input);
            return actions;
        }

        if (input.IsIdle != _segment.IsIdle)
        {
            actions.Add(CloseCurrent(
                input.IsIdle
                    ? SessionCloseReasons.Idle
                    : SessionCloseReasons.Resume,
                input.NowLocal,
                input.NowUtcMs,
                input.MonotonicTimestamp));

            TryOpen(input, actions);
            RememberTick(input);
            return actions;
        }

        if (!input.IsIdle &&
            input.Window is not null &&
            input.Identity is not null &&
            input.Window != _segment.Window)
        {
            actions.Add(CloseCurrent(
                SessionCloseReasons.WindowChanged,
                input.NowLocal,
                input.NowUtcMs,
                input.MonotonicTimestamp));

            TryOpen(input, actions);
            RememberTick(input);
            return actions;
        }

        var sinceHeartbeat = SecondsBetween(
            _segment.LastHeartbeatMonotonic,
            input.MonotonicTimestamp);

        if (sinceHeartbeat >= input.Settings.CheckpointSeconds)
        {
            actions.Add(CreateAction(
                SessionSegmentActionKind.Heartbeat,
                _segment,
                input.NowLocal,
                input.NowUtcMs,
                input.MonotonicTimestamp,
                ""));

            RememberTick(input);
            return actions;
        }

        RememberTick(input);
        return actions;
    }

    public SessionSegmentAction? Suspend(
        string reason,
        DateTimeOffset nowLocal,
        long nowUtcMs,
        long monotonicTimestamp)
    {
        _isSuspended = true;
        _suspendedTickCount = 0;

        if (_segment is null)
            return null;

        return CloseCurrent(
            reason,
            nowLocal,
            nowUtcMs,
            monotonicTimestamp);
    }

    public IReadOnlyList<SessionSegmentAction> Resume(
        SessionTickInput input)
    {
        _isSuspended = false;
        _suspendedTickCount = 0;
        var actions = new List<SessionSegmentAction>();

        if (_segment is null)
            TryOpen(input, actions);

        RememberTick(input);
        return actions;
    }

    public void ClearSuspended()
    {
        _isSuspended = false;
        _suspendedTickCount = 0;
        _lastTickLocal = null;
        _lastTickMonotonic = null;
        _lastTickUtcMs = 0;
    }

    public SessionSegmentAction? Close(
        string reason,
        DateTimeOffset nowLocal,
        long nowUtcMs,
        long monotonicTimestamp)
    {
        if (_segment is null)
            return null;

        return CloseCurrent(
            reason,
            nowLocal,
            nowUtcMs,
            monotonicTimestamp);
    }

    public void MarkHeartbeatDispatched(
        Guid sessionKey,
        DateTimeOffset heartbeatLocal,
        long heartbeatMonotonic)
    {
        if (_segment is null ||
            _segment.SessionKey != sessionKey)
        {
            return;
        }

        _segment.LastHeartbeatLocal = heartbeatLocal;
        _segment.LastHeartbeatMonotonic = heartbeatMonotonic;
    }

    public SessionSegmentAction? GetSnapshot(
        DateTimeOffset nowLocal,
        long nowUtcMs,
        long monotonicTimestamp)
    {
        if (_segment is null)
            return null;

        return CreateAction(
            SessionSegmentActionKind.Heartbeat,
            _segment,
            nowLocal,
            nowUtcMs,
            monotonicTimestamp,
            "");
    }

    private void TryOpen(
        SessionTickInput input,
        ICollection<SessionSegmentAction> actions)
    {
        if (input.IsIdle)
        {
            OpenAt(
                input.NowLocal,
                input.NowUtcMs,
                input.MonotonicTimestamp,
                null,
                AppIdentity.Idle,
                true,
                actions);
            return;
        }

        if (input.Window is null || input.Identity is null)
            return;

        OpenAt(
            input.NowLocal,
            input.NowUtcMs,
            input.MonotonicTimestamp,
            input.Window,
            input.Identity,
            false,
            actions);
    }

    private void OpenAt(
        DateTimeOffset startLocal,
        long startUtcMs,
        long monotonicTimestamp,
        WindowInfo? window,
        AppIdentity identity,
        bool isIdle,
        ICollection<SessionSegmentAction> actions)
    {
        _segment = new SegmentState(
            Guid.NewGuid(),
            startLocal,
            startUtcMs,
            monotonicTimestamp,
            window,
            identity,
            isIdle,
            GetNextLocalMidnight(startLocal));

        actions.Add(CreateAction(
            SessionSegmentActionKind.Open,
            _segment,
            startLocal,
            startUtcMs,
            monotonicTimestamp,
            ""));
    }

    private SessionSegmentAction CloseCurrent(
        string reason,
        DateTimeOffset endLocal,
        long endUtcMs,
        long monotonicTimestamp)
    {
        var segment = _segment ??
            throw new InvalidOperationException("没有可关闭的会话段。");

        var action = CreateAction(
            SessionSegmentActionKind.Close,
            segment,
            endLocal,
            endUtcMs,
            monotonicTimestamp,
            reason);

        _segment = null;
        return action;
    }

    private static SessionSegmentAction CreateAction(
        SessionSegmentActionKind kind,
        SegmentState segment,
        DateTimeOffset endLocal,
        long endUtcMs,
        long monotonicTimestamp,
        string closeReason)
    {
        var duration = Math.Max(
            0,
            (int)Math.Floor(
                SecondsBetween(
                    segment.MonotonicStart,
                    monotonicTimestamp)));

        return new SessionSegmentAction(
            kind,
            segment.SessionKey,
            segment.PersistedRowId,
            segment.StartLocal,
            segment.StartUtcMs,
            endLocal,
            Math.Max(segment.StartUtcMs, endUtcMs),
            duration,
            segment.StartLocal.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            segment.Window,
            segment.Identity,
            segment.IsIdle,
            closeReason);
    }

    private static SessionSegmentAction CreateRefreshAction(
        SessionTickInput input)
    {
        return new SessionSegmentAction(
            SessionSegmentActionKind.RefreshForeground,
            Guid.Empty,
            null,
            input.NowLocal,
            input.NowUtcMs,
            input.NowLocal,
            input.NowUtcMs,
            0,
            input.NowLocal.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            null,
            AppIdentity.Idle,
            false,
            "");
    }

    private void RememberTick(SessionTickInput input)
    {
        _lastTickLocal = input.NowLocal;
        _lastTickUtcMs = input.NowUtcMs;
        _lastTickMonotonic = input.MonotonicTimestamp;
    }

    private static DateTimeOffset GetNextLocalMidnight(
        DateTimeOffset localTime)
    {
        var nextDate = localTime.Date.AddDays(1);
        var offset = TimeZoneInfo.Local.GetUtcOffset(nextDate);
        return new DateTimeOffset(nextDate, offset);
    }

    private static double SecondsBetween(long start, long end)
    {
        return Math.Max(
            0,
            (end - start) / (double)Stopwatch.Frequency);
    }

    private static long ToMonotonicTicks(double seconds)
    {
        return (long)Math.Round(seconds * Stopwatch.Frequency);
    }

    private sealed class SegmentState
    {
        public SegmentState(
            Guid sessionKey,
            DateTimeOffset startLocal,
            long startUtcMs,
            long monotonicStart,
            WindowInfo? window,
            AppIdentity identity,
            bool isIdle,
            DateTimeOffset nextMidnightLocal)
        {
            SessionKey = sessionKey;
            StartLocal = startLocal;
            StartUtcMs = startUtcMs;
            MonotonicStart = monotonicStart;
            Window = window;
            Identity = identity;
            IsIdle = isIdle;
            NextMidnightLocal = nextMidnightLocal;
            LastHeartbeatLocal = startLocal;
            LastHeartbeatMonotonic = monotonicStart;
        }

        public Guid SessionKey { get; }
        public DateTimeOffset StartLocal { get; }
        public long StartUtcMs { get; }
        public long MonotonicStart { get; }
        public WindowInfo? Window { get; }
        public AppIdentity Identity { get; }
        public bool IsIdle { get; }
        public DateTimeOffset NextMidnightLocal { get; }
        public long? PersistedRowId { get; set; }
        public DateTimeOffset LastHeartbeatLocal { get; set; }
        public long LastHeartbeatMonotonic { get; set; }
    }
}
