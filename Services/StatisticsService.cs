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

    //统计依赖于数据库
    public StatisticsService(
        ActivityRepository repository)
    {
        _repository = repository;
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
                    Seconds = seconds
                };
            })

            // 防止极端情况下出现 0 秒记录
            .Where(x => x.Seconds > 0);

        // 第三步：
        // 按应用进行分组
        var result = pieces
            .GroupBy(
                x => GetApplicationKey(
                    x.ExecutablePath,
                    x.ProcessName),
                StringComparer.OrdinalIgnoreCase)

            // 第四步：
            // 每组累计时间
            .Select(group =>
            {
                var first = group.First();

                return new AppUsageStat
                {
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

    // 确定一个应用的唯一识别 Key
    private static string GetApplicationKey(
        string executablePath,
        string processName)
    {
        // 优先使用 exe 完整路径
        if (!string.IsNullOrWhiteSpace(
                executablePath))
        {
            return executablePath.Trim();
        }

        // 某些系统进程无法获取路径，
        // 就退回使用进程名
        return "PROCESS:" +
               processName.Trim();
    }
}