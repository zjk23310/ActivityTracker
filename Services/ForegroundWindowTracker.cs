using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ActivityTracker.Services;

public sealed class ForegroundWindowTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private readonly WinEventDelegate _callback;
    private IntPtr _hook;

    public event Action<WindowInfo>? ForegroundChanged;

    public ForegroundWindowTracker()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        var current = GetForegroundWindow();
        if (current != IntPtr.Zero)
            RaiseWindowInfo(current);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd != IntPtr.Zero)
            RaiseWindowInfo(hwnd);
    }

    private void RaiseWindowInfo(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);

            var titleLength = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(titleLength + 1);
            GetWindowText(hwnd, sb, sb.Capacity);

            string path;
            try { path = process.MainModule?.FileName ?? ""; }
            catch { path = ""; }

            ForegroundChanged?.Invoke(new WindowInfo(
                process.ProcessName,
                sb.ToString(),
                path));
        }
        catch
        {
            // 进程可能在读取期间退出，忽略即可。
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed record WindowInfo(string ProcessName, string WindowTitle, string ExecutablePath);
