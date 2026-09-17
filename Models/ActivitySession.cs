using System;
namespace ActivityTracker.Models;

public sealed class ActivitySession
{
    public long Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    //可执行路径
    public string ExecutablePath { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationSeconds { get; set; }
    //是否是空闲操作
    public bool IsIdle { get; set; }
}
