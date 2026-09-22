using System;

using Microsoft.Extensions.Logging;

using Forms = System.Windows.Forms;

namespace ActivityTracker.Services;

// 系统托盘图标和右键菜单。
//
// 这里只表达"用户点了什么"，不直接操作主窗口：
// 主窗口需要一个托盘服务来弹气泡提示，如果托盘又反过来
// 持有主窗口，两边就绕成循环依赖了。
// 所以托盘只抛事件，由 App 负责把事件接到具体窗口上。
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;//NotifyIcon 是 WinForms 的托盘图标类，WPF 没有自带的托盘图标类，所以这里用 WinForms 的
    private readonly Forms.ContextMenuStrip _menu;//ContextMenuStrip 是 WinForms 的右键菜单类，WPF 没有自带的右键菜单类，所以这里用 WinForms 的
    private readonly ILogger<TrayService> _logger;

    // 气泡提示只弹一次
    private bool _tipShown;

    // 允许提前释放：退出时要先摘掉图标，
    // 之后容器还会再释放一次，所以必须幂等
    private bool _disposed;

    // 用户要求显示主窗口（双击图标或点"显示"）
    public event Action? ShowRequested;

    // 用户要求真正退出程序
    public event Action? ExitRequested;

    public TrayService(ILogger<TrayService> logger)
    {
        _logger = logger;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "ActivityTracker",
            Icon =
                System.Drawing.SystemIcons.Application,
            Visible = true
        };

        // 双击托盘图标恢复窗口
        _notifyIcon.DoubleClick +=//DoubleClick 事件在托盘图标被双击时触发
            (_, _) => ShowRequested?.Invoke();//?.Invoke() 是 C# 6.0 的空条件运算符，表示如果 ShowRequested 不为 null，则调用它。(, _) => 表示忽略事件参数

        // 创建右键菜单
        _menu = new Forms.ContextMenuStrip();//new新类

        var showItem =
            new Forms.ToolStripMenuItem("显示");

        showItem.Click +=
            (_, _) => ShowRequested?.Invoke();

        var exitItem =
            new Forms.ToolStripMenuItem("退出");

        exitItem.Click +=
            (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(showItem);
        _menu.Items.Add(
            new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = _menu;

        _logger.LogInformation("系统托盘已创建。");
    }

    // 第一次隐藏到托盘时提醒用户，之后不再打扰
    public void ShowTrayTipOnce()
    {
        if (_disposed || _tipShown) return;

        _tipShown = true;

        _notifyIcon.ShowBalloonTip(
            2000,
            "ActivityTracker",
            "程序仍在后台运行，双击托盘图标可重新打开。",
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        // 可能被调用两次（退出时提前摘一次，
        // 容器释放时再一次），所以这里要幂等
        //这里是为了提前摘掉图表，防止出现比较奇怪的现象
        if (_disposed) return;

        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Dispose();
        _menu.Dispose();

        _logger.LogInformation("系统托盘已释放。");
    }
}
