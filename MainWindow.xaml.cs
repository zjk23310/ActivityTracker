using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.ComponentModel;
using ActivityTracker.Data;
using ActivityTracker.Services;

using Forms = System.Windows.Forms;

namespace ActivityTracker;

public partial class MainWindow : Window
{
    private readonly ActivityRepository _repository;
    private readonly SessionTracker _sessionTracker;

    // 系统托盘图标
    private readonly Forms.NotifyIcon _notifyIcon;

    // 是否真正退出程序
    private bool _reallyExit = false;

    // 避免每次点 X 都弹提示
    private bool _trayTipShown = false;

    public MainWindow()
    {
        InitializeComponent();

        // ==============================
        // 数据库
        // ==============================
        var dbPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ActivityTracker",
            "activity.db");

        _repository = new ActivityRepository(dbPath);

        // 5 分钟没有键鼠输入则判断为空闲
        _sessionTracker = new SessionTracker(
            _repository,
            TimeSpan.FromMinutes(5));

        _sessionTracker.SessionChanged += () =>
            Dispatcher.Invoke(RefreshData);

        // ==============================
        // 系统托盘
        // ==============================
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "ActivityTracker",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        // 双击托盘图标恢复窗口
        _notifyIcon.DoubleClick += (_, _) =>
        {
            ShowMainWindow();
        };

        // 创建右键菜单
        var menu = new Forms.ContextMenuStrip();

        var showItem = new Forms.ToolStripMenuItem("显示");
        showItem.Click += (_, _) =>
        {
            ShowMainWindow();
        };

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            ExitApplication();
        };

        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;

        // ==============================
        // 窗口加载
        // ==============================
        Loaded += (_, _) =>
        {
            _sessionTracker.Start();
            RefreshData();
        };
    }

    // ==============================
    // 点击刷新按钮
    // ==============================
    private void Refresh_Click(
        object sender,
        RoutedEventArgs e)
    {
        RefreshData();
    }

    // ==============================
    // 刷新统计数据
    // ==============================
    private void RefreshData()
    {
        var sessions = _repository.GetToday();

        ActivityGrid.ItemsSource = sessions;

        var total = sessions.Sum(
            x => x.DurationSeconds);

        var active = sessions
            .Where(x => !x.IsIdle)
            .Sum(x => x.DurationSeconds);

        SummaryText.Text =
            $"今日记录 {Format(total)}，活跃 {Format(active)}";
    }

    // ==============================
    // 格式化时间
    // ==============================
    private static string Format(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        return
            $"{(int)span.TotalHours:D2}:" +
            $"{span.Minutes:D2}:" +
            $"{span.Seconds:D2}";
    }

    // ==============================
    // 恢复主窗口
    // ==============================
    private void ShowMainWindow()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // 让窗口显示到最前面
        Topmost = true;
        Topmost = false;

        Focus();
    }

    // ==============================
    // 点击右上角 X
    // ==============================
    protected override void OnClosing(
        CancelEventArgs e)
    {
        // 如果不是用户主动选择“退出”
        if (!_reallyExit)
        {
            // 阻止窗口真正关闭
            e.Cancel = true;

            // 隐藏窗口
            Hide();

            // 第一次隐藏时提示
            if (!_trayTipShown)
            {
                _notifyIcon.ShowBalloonTip(
                    2000,
                    "ActivityTracker",
                    "程序仍在后台运行，双击托盘图标可重新打开。",
                    Forms.ToolTipIcon.Info);

                _trayTipShown = true;
            }

            return;
        }

        base.OnClosing(e);
    }

    // ==============================
    // 托盘菜单 → 退出
    // ==============================
    private void ExitApplication()
    {
        _reallyExit = true;

        // 关闭窗口
        Close();

        // 因为使用 OnExplicitShutdown
        // 所以这里明确退出 WPF 程序
        System.Windows.Application.Current.Shutdown();
    }

    // ==============================
    // 真正关闭程序
    // ==============================
    protected override void OnClosed(EventArgs e)
    {
        // 停止应用追踪并保存当前 Session
        _sessionTracker.Dispose();

        // 清理托盘图标
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        base.OnClosed(e);
    }
}