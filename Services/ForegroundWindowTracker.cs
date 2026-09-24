using System;//会提供一些基础数据
using System.Diagnostics;//操作进程
using System.Runtime.InteropServices;
using System.Text;

namespace ActivityTracker.Services;


public sealed class ForegroundWindowTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;//对应
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private readonly WinEventDelegate _callback;
    private IntPtr _hook;//钩子

    //事件：窗口改变
    public event Action<WindowInfo>? ForegroundChanged;

    public ForegroundWindowTracker()
    {
        _callback = OnWinEvent;//赋值，callback
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;//防止重复启动如果hook==Zero表示还没有注册hook

        _hook = SetWinEventHook(//设置hook
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,//最小事件和最大事件一样说明只监听前台窗口变化
            IntPtr.Zero,
            _callback,//检测到事件，回调什么函数
            0,//进程id
            0,//线程id 0表示不限制具体
            WINEVENT_OUTOFCONTEXT);//不把代码注入其他进程自己处理

        var current = GetForegroundWindow();//立即获取当前前台窗口句柄，防止启动时没有触发事件
        if (current != IntPtr.Zero)//成功获得窗口句柄
            RaiseWindowInfo(current);
    }


    //回调函数，前台窗口发生变化时触发
    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,//新前台窗口句柄
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd != IntPtr.Zero)
            RaiseWindowInfo(hwnd);
    }

    // 睡眠恢复、解锁或心跳断层后主动重读前台窗口，
    // 不依赖 Windows 一定会再次发送前台切换事件。
    public void Refresh()
    {
        var current = GetForegroundWindow();
        if (current != IntPtr.Zero)
            RaiseWindowInfo(current);
    }

    private void RaiseWindowInfo(IntPtr hwnd)//从一个窗口句柄，解析出这个窗口属于哪个应用。
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);//获取pid
            using var process = Process.GetProcessById((int)pid);//根据pid获取进程。包含释放操作

            var titleLength = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(titleLength + 1);
            GetWindowText(hwnd, sb, sb.Capacity);

            string path;
            try { path = process.MainModule?.FileName ?? ""; }
            catch { path = ""; }//获取完整路径

            ForegroundChanged?.Invoke(new WindowInfo(//最终流出，进程名，标题名以及可执行文件路径,?表示若不为空则执行，也就是订阅之后执行
                process.ProcessName,
                sb.ToString(),
                path,
                (int)pid));
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
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);//委托类型，定义了回调函数的签名

    //向windows注册一个事件钩子，监听前台窗口变化
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,//没有额外DLL模块？
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);//eventMin和Max确定了范围，只找前端发生变化的页面

    //取消注册事件钩子
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    //获取当前前台窗口句柄
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    //获取窗口所属进程id
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    //获取窗口标题
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    //获取窗口标题长度
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

//本质是个类或结构体
// 这里只保存从前台窗口直接取得的原始信息。
// ProcessId 供 AppIdentityResolver 在进程仍存活时读取 Windows 包身份，
// 不写入活动记录，也不承担应用身份判断职责。
public sealed record WindowInfo(
    string ProcessName,
    string WindowTitle,
    string ExecutablePath,
    int ProcessId);
