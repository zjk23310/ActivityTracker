using System;
using System.Collections.Generic;
using System.Linq;

using ActivityTracker.Configuration;
using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

public sealed class StatisticsService
{
    private readonly ActivityRepository _repository;
    private readonly AppIdentityResolver _appIdentityResolver;
    private readonly SettingsService _settingsService;
    private readonly ILogger<StatisticsService> _logger;

    public StatisticsService(
        ActivityRepository repository,
        AppIdentityResolver appIdentityResolver,
        SettingsService settingsService,
        ILogger<StatisticsService> logger)
    {
        _repository = repository;
        _appIdentityResolver = appIdentityResolver;
        _settingsService = settingsService;
        _logger = logger;
    }

    // 返回已经按 UTC 毫秒裁剪并应用超长保护的会话。
    // 数据库查询明确排除 IsClosed=0，当前在途段只从 currentSnapshot 加入一次。
    public List<ActivitySession> GetActivitySessions(
        DateTime rangeStart,
        DateTime rangeEnd,
        ActivitySession? currentSnapshot = null)
    {
        ValidateRange(rangeStart, rangeEnd);

        var rangeStartMs = ToUtcMilliseconds(rangeStart);
        var rangeEndMs = ToUtcMilliseconds(rangeEnd);
        var sessions = _repository.GetRange(
            rangeStart,
            rangeEnd,
            includeOpen: false);

        if (currentSnapshot is not null &&
            currentSnapshot.StartUtcMs < rangeEndMs &&
            currentSnapshot.EndUtcMs > rangeStartMs)
        {
            sessions.Add(currentSnapshot);
        }

        var maxSeconds =
            _settingsService.Current.Tracking.MaxSessionMinutes * 60;

        var clipped = sessions
            .Select(session => ClipAndClamp(
                session,
                rangeStartMs,
                rangeEndMs,
                maxSeconds))
            .Where(session => session is not null)
            .Select(session => session!)
            .OrderBy(session => session.StartUtcMs)
            .ThenByDescending(session => session.EndUtcMs)
            .ThenBy(session => session.Id)
            .ToList();

        return RemoveOverlaps(clipped);
    }

    public List<AppUsageStat> GetAppUsage(
        DateTime rangeStart,
        DateTime rangeEnd,
        ActivitySession? currentSnapshot = null)
    {
        var sessions = GetActivitySessions(
            rangeStart,
            rangeEnd,
            currentSnapshot);
        var legacyIdentityCache =
            new Dictionary<string, AppIdentity>(
                StringComparer.OrdinalIgnoreCase);

        return sessions
            .Where(session => !session.IsIdle)
            .Select(session => new
            {
                session.ProcessName,
                session.ExecutablePath,
                Identity = ResolveIdentity(
                    session,
                    legacyIdentityCache),
                Seconds = session.DurationSeconds
            })
            .Where(piece => piece.Seconds > 0)
            .GroupBy(
                piece => piece.Identity.AppId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new AppUsageStat
                {
                    AppId = first.Identity.AppId,
                    AppName = first.Identity.AppName,
                    ProcessName = first.ProcessName,
                    ExecutablePath = first.ExecutablePath,
                    TotalSeconds = group.Sum(piece => piece.Seconds),
                    SessionCount = group.Count()
                };
            })
            .OrderByDescending(item => item.TotalSeconds)
            .ToList();
    }

    private ActivitySession? ClipAndClamp(
        ActivitySession session,
        long rangeStartMs,
        long rangeEndMs,
        int maxSeconds)
    {
        var reliableDuration = Math.Max(
            0,
            Math.Min(session.DurationSeconds, maxSeconds));
        var reliableEndMs = Math.Min(
            session.EndUtcMs,
            session.StartUtcMs + reliableDuration * 1000L);
        var actualStartMs = Math.Max(
            session.StartUtcMs,
            rangeStartMs);
        var actualEndMs = Math.Min(
            reliableEndMs,
            rangeEndMs);

        if (actualEndMs <= actualStartMs)
            return null;

        var overlapSeconds = Math.Max(
            0,
            (int)((actualEndMs - actualStartMs) / 1000));
        var duration = Math.Min(
            overlapSeconds,
            maxSeconds);

        if (session.DurationSeconds > maxSeconds)
        {
            _logger.LogWarning(
                "检测到超长历史会话并在展示/统计时截断：Id={SessionId}，原时长={DurationSeconds} 秒，上限={MaxSeconds} 秒。",
                session.Id,
                session.DurationSeconds,
                maxSeconds);
        }

        if (duration <= 0)
            return null;

        var endMs = actualStartMs + duration * 1000L;
        return CopyWithRange(
            session,
            actualStartMs,
            endMs,
            duration);
    }

    // 时钟回拨可能让相邻原始行的 UTC 区间重叠。统计读侧只计算每个毫秒一次，
    // 原始数据库记录保持不变，便于后续审计或改进恢复算法。
    private static List<ActivitySession> RemoveOverlaps(
        IReadOnlyList<ActivitySession> sessions)
    {
        var result = new List<ActivitySession>(sessions.Count);
        long? coveredUntilMs = null;

        foreach (var session in sessions)
        {
            var effectiveStartMs = coveredUntilMs.HasValue
                ? Math.Max(
                    session.StartUtcMs,
                    coveredUntilMs.Value)
                : session.StartUtcMs;

            if (session.EndUtcMs <= effectiveStartMs)
                continue;

            var duration = (int)(
                (session.EndUtcMs - effectiveStartMs) / 1000L);
            if (duration <= 0)
                continue;

            var effectiveEndMs =
                effectiveStartMs + duration * 1000L;
            result.Add(CopyWithRange(
                session,
                effectiveStartMs,
                effectiveEndMs,
                duration));
            coveredUntilMs = effectiveEndMs;
        }

        return result;
    }

    private static ActivitySession CopyWithRange(
        ActivitySession session,
        long startUtcMs,
        long endUtcMs,
        int durationSeconds)
    {
        var startLocal = DateTimeOffset
            .FromUnixTimeMilliseconds(startUtcMs)
            .ToLocalTime()
            .LocalDateTime;
        var endLocal = DateTimeOffset
            .FromUnixTimeMilliseconds(endUtcMs)
            .ToLocalTime()
            .LocalDateTime;

        return new ActivitySession
        {
            Id = session.Id,
            ProcessName = session.ProcessName,
            WindowTitle = session.WindowTitle,
            ExecutablePath = session.ExecutablePath,
            AppId = session.AppId,
            AppName = session.AppName,
            StartTime = startLocal,
            EndTime = endLocal,
            DurationSeconds = durationSeconds,
            IsIdle = session.IsIdle,
            SessionKey = session.SessionKey,
            StartUtcMs = startUtcMs,
            EndUtcMs = endUtcMs,
            LocalDate = session.LocalDate,
            IsClosed = session.IsClosed,
            CloseReason = session.CloseReason,
            IsRecovered = session.IsRecovered,
            Source = session.Source
        };
    }

    private AppIdentity ResolveIdentity(
        ActivitySession session,
        IDictionary<string, AppIdentity> legacyIdentityCache)
    {
        if (!string.IsNullOrWhiteSpace(session.AppId))
        {
            var appName = session.AppName;
            if (string.IsNullOrWhiteSpace(appName))
            {
                appName = _appIdentityResolver.Resolve(
                        session.ProcessName,
                        session.ExecutablePath)
                    .AppName;
            }

            return new AppIdentity(
                session.AppId.Trim(),
                appName,
                AppIdentitySource.Persisted);
        }

        var legacyKey =
            !string.IsNullOrWhiteSpace(session.ExecutablePath)
                ? "path:" + session.ExecutablePath.Trim()
                : "process:" + session.ProcessName.Trim();

        if (legacyIdentityCache.TryGetValue(
                legacyKey,
                out var cachedIdentity))
        {
            return cachedIdentity;
        }

        var resolved = _appIdentityResolver.Resolve(
            session.ProcessName,
            session.ExecutablePath);
        legacyIdentityCache[legacyKey] = resolved;
        return resolved;
    }

    private static void ValidateRange(
        DateTime start,
        DateTime end)
    {
        if (end <= start)
        {
            throw new ArgumentException(
                "结束时间必须晚于开始时间。");
        }
    }

    private static long ToUtcMilliseconds(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Local)
            : value;
        return new DateTimeOffset(local)
            .ToUniversalTime()
            .ToUnixTimeMilliseconds();
    }
}
