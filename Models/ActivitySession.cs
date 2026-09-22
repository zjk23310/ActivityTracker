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

    // 是否为用户空闲片段。
    public bool IsIdle { get; set; }
}
