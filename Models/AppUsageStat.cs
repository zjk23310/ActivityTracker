using System;

namespace ActivityTracker.Models;

public sealed class AppUsageStat
{
    // 用于界面显示
    public string ProcessName { get; init; } = "";

    // 用于识别应用
    public string ExecutablePath { get; init; } = "";

    // 查询时间范围内累计使用秒数
    public int TotalSeconds { get; init; }

    // 该时间范围内出现了多少个会话段
    public int SessionCount { get; init; }

    // 给界面直接绑定
    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromSeconds(TotalSeconds);//将总秒数转换为TimeSpan对象

            return $"{(int)span.TotalHours:D2}:" +
                   $"{span.Minutes:D2}:" +
                   $"{span.Seconds:D2}";
        }
    }
}