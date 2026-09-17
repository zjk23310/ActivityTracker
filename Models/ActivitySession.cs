using System;
namespace ActivityTracker.Models;

public sealed class ActivitySession
{
    public long Id { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsIdle { get; set; }
}
