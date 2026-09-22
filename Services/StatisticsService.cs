using System;
using System.Collections.Generic;
using System.Linq;

using ActivityTracker.Data;
using ActivityTracker.Models;

namespace ActivityTracker.Services;

//统计服务
public sealed class StatisticsService
{
    private readonly ActivityRepository _repository;
    private readonly AppIdentityResolver _appIdentityResolver;

    //统计依赖于数据库
    public StatisticsService(
        ActivityRepository repository,
        AppIdentityResolver appIdentityResolver)
    {
        _repository = repository;
        _appIdentityResolver = appIdentityResolver;
    }

    // 统计指定时间范围内各应用使用时长
    public List<AppUsageStat> GetAppUsage(
        DateTime rangeStart,
        DateTime rangeEnd,
        ActivitySession? currentSnapshot = null)
    {
        if (rangeEnd <= rangeStart)
        {
            throw new ArgumentException(
                "结束时间必须晚于开始时间。");
        }

        // 第一步：从数据库中找出
        // 所有和查询时间范围有交集的记录
        var sessions =
            _repository.GetRange(
                rangeStart,
                rangeEnd);
        //将现在的也加入到sessions
        if (currentSnapshot is not null &&
        currentSnapshot.StartTime < rangeEnd &&
        currentSnapshot.EndTime > rangeStart)
        {
            sessions.Add(currentSnapshot);
        }

        // 历史记录可能很多，同一路径只解析一次版本资源。
        var legacyIdentityCache =
            new Dictionary<string, AppIdentity>(
                StringComparer.OrdinalIgnoreCase);


        // 第二步：
        // 排除 IDLE，并裁剪时间范围
        var pieces = sessions
            .Where(session => !session.IsIdle)
            .Select(session =>
            {
                // Session 开始得太早，
                // 就从查询范围开始算
                var actualStart =
                    session.StartTime < rangeStart
                        ? rangeStart
                        : session.StartTime;

                // Session 结束得太晚，
                // 就只算到查询范围结束
                var actualEnd =
                    session.EndTime > rangeEnd
                        ? rangeEnd
                        : session.EndTime;

                var seconds =
                    Math.Max(
                        0,
                        (int)(actualEnd - actualStart)
                            .TotalSeconds);

                return new
                {
                    session.ProcessName,
                    session.ExecutablePath,
                    Identity = ResolveIdentity(
                        session,
                        legacyIdentityCache),
                    Seconds = seconds
                };
            })

            // 防止极端情况下出现 0 秒记录
            .Where(x => x.Seconds > 0);

        // 第三步：
        // 按应用进行分组
        var result = pieces
            .GroupBy(
                x => x.Identity.AppId,
                StringComparer.OrdinalIgnoreCase)

            // 第四步：
            // 每组累计时间
            .Select(group =>
            {
                var first = group.First();

                return new AppUsageStat
                {
                    AppId =
                        first.Identity.AppId,

                    AppName =
                        first.Identity.AppName,

                    ProcessName =
                        first.ProcessName,

                    ExecutablePath =
                        first.ExecutablePath,

                    TotalSeconds =
                        group.Sum(
                            x => x.Seconds),

                    SessionCount =
                        group.Count()
                };
            })

            // 使用时间最长的排在最上面
            .OrderByDescending(
                x => x.TotalSeconds)

            .ToList();

        return result;
    }

    private AppIdentity ResolveIdentity(
        ActivitySession session,
        IDictionary<string, AppIdentity> legacyIdentityCache)
    {
        // 新记录直接使用已经落库的逻辑身份。
        if (!string.IsNullOrWhiteSpace(session.AppId))
        {
            var appName = session.AppName;

            // 兼容未来可能出现的“已有 AppId、缺少显示名”记录。
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
                AppIdentitySource.Persisted);// Persisted 表示已经落库的记录
        }

        // 历史记录没有 AppId：通过同一个 Resolver 即时解析。
        // 即使原 exe 已删除，Resolver 也会退回路径或进程名，
        // 因此旧记录仍然会出现在统计结果中。
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
}
