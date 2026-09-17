using System;
using System.Runtime.InteropServices;

namespace ActivityTracker.Services;

public static class IdleDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        // GetLastInputInfo 使用 32 位系统 TickCount；按 uint 做减法可正确处理约 49.7 天的回绕。
        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
