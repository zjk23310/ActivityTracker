using System;
using System.Runtime.InteropServices;

namespace ActivityTracker.Services;

public static class IdleDetector
{

    //按照 Windows API 官方规定的 C 结构体，一项一项翻译过来的。
    [StructLayout(LayoutKind.Sequential)]//按照在代码中写字段的顺序，把这个结构体放进内存。
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    // 获取最后一次输入的时间
    //ref是引用，不是副本
    //bool是返回值，表示函数是否成功执行
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // 获取系统空闲时间
    //TimeSpan是一个表示时间间隔的结构体，是系统提供的一个类型
    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO
        {
            // 获取结构体的大小
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()//告诉windows传给的这个结构体大小是 8 字节
        };
        
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        // GetLastInputInfo 使用 32 位系统 TickCount；按 uint 做减法可正确处理约 49.7 天的回绕。
        var now = unchecked((uint)Environment.TickCount);//unchecked表示不检查溢出，直接返回结果.Environment.TickCount是系统启动以来的毫秒数，uint表示无符号整数
        var elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
