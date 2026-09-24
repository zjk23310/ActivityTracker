using System;

namespace ActivityTracker.Models;

public sealed class ActivitySession
{
    public long Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";

    // 前台进程报告的原始可执行文件路径。
    public string ExecutablePath { get; set; } = "";

    // 由 AppIdentityResolver 解析出的逻辑应用身份。
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationSeconds { get; set; }

    // v2 可靠性字段。UTC 毫秒用于比较和查询，本地 ISO 时间继续用于显示及兼容旧数据。
    public string SessionKey { get; set; } = "";
    public long StartUtcMs { get; set; }
    public long EndUtcMs { get; set; }
    public string LocalDate { get; set; } = "";
    public bool IsClosed { get; set; } = true;
    public string CloseReason { get; set; } = "";
    public bool IsRecovered { get; set; }
    public string Source { get; set; } = "live";

    // 是否为用户空闲片段。
    public bool IsIdle { get; set; }
}
