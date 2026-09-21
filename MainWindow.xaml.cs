using ActivityTracker.Data;
using ActivityTracker.Models;
using ActivityTracker.Services;
using ActivityTracker.Views;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace ActivityTracker;

public partial class MainWindow : Window
{
    private readonly ActivityRepository _repository;
    private readonly SessionTracker _sessionTracker;

    // 点 X 隐藏到托盘时要弹一次气泡提示
    private readonly TrayService _trayService;

    // 是否真正退出程序
    private bool _reallyExit = false;

    // 窗口是否已经关闭。
    // 退出过程中托盘和管道的事件可能晚到，
    // 那时窗口已经没了，再调用 Show 会抛
    // “关闭窗口后，无法设置可见性” 的异常。
    private bool _closed = false;

    //主窗口需要依赖注入的参数，构造函数里不再 new 这些对象，而是由容器传进来
    public MainWindow(
        ActivityRepository repository,
        DailyRepository dailyRepository,
        TodoRepository todoRepository,
        SessionTracker sessionTracker,
        StatisticsService statisticsService,
        TrayService trayService)
    {
        InitializeComponent();

        _repository = repository;
        _sessionTracker = sessionTracker;
        _trayService = trayService;

        // ==============================
        // 接入统计页面
        // ==============================
        StatisticsHost.Content =
            new StatisticsView(
                statisticsService,
                _sessionTracker);

        // ==============================
        // 接入日记与待办页面
        // ==============================
        DiaryTodoHost.Content =
            new DiaryTodoView(
                dailyRepository,
                todoRepository);

        // 活动记录变化时刷新原来的表格
        _sessionTracker.SessionChanged +=
            OnSessionChanged;

        // ==============================
        // 窗口加载
        //
        // 追踪的启动已经不在这里了。
        // 现在由 TrackingHostedService 在进程启动时负责，
        // 所以主窗口没显示也不影响记录，
        // 窗口重新创建也不会重复起一个追踪器。
        // ==============================
        Loaded += (_, _) => RefreshData();
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
    // 刷新活动记录数据
    // ==============================
    private void RefreshData()
    {
        var rangeStart = DateTime.Today;
        var rangeEnd = rangeStart.AddDays(1);

        var sessions =
            _repository.GetRange(
                rangeStart,
                rangeEnd);

        // 当前会话尚未写入 SQLite，也要显示并计入汇总。
        var current =
            _sessionTracker.GetCurrentSnapshot();

        if (current is not null &&
            current.StartTime < rangeEnd &&
            current.EndTime > rangeStart)
        {
            sessions.Add(current);
        }

        // 会话可能跨过午夜，只显示并统计与今天重叠的部分。
        var visibleSessions = sessions
            .Select(session =>
                ClipToRange(
                    session,
                    rangeStart,
                    rangeEnd))
            .Where(session => session is not null)
            .Select(session => session!)
            .OrderByDescending(session => session.StartTime)
            .ToList();

        ActivityGrid.ItemsSource =
            visibleSessions;

        var total =
            visibleSessions.Sum(
                x => x.DurationSeconds);

        var active =
            visibleSessions
                .Where(x => !x.IsIdle)
                .Sum(x => x.DurationSeconds);

        SummaryText.Text =
            $"今日记录 {Format(total)}，" +
            $"活跃 {Format(active)}";
    }


    private static ActivitySession? ClipToRange(
        ActivitySession session,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var actualStart =
            session.StartTime < rangeStart
                ? rangeStart
                : session.StartTime;

        var actualEnd =
            session.EndTime > rangeEnd
                ? rangeEnd
                : session.EndTime;

        if (actualEnd <= actualStart)
            return null;

        return new ActivitySession
        {
            Id = session.Id,
            ProcessName = session.ProcessName,
            WindowTitle = session.WindowTitle,
            ExecutablePath = session.ExecutablePath,
            StartTime = actualStart,
            EndTime = actualEnd,
            DurationSeconds =
                (int)(actualEnd - actualStart).TotalSeconds,
            IsIdle = session.IsIdle
        };
    }


    // SessionChanged 可能从窗口钩子线程或计时器线程触发。
    // 异步投递到 UI 线程，避免追踪线程等待界面查询完成。
    private void OnSessionChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (!_closed)
                RefreshData();
        });
    }


    // ==============================
    // 格式化时间
    // ==============================
    private static string Format(
        int seconds)
    {
        var span =
            TimeSpan.FromSeconds(seconds);

        return
            $"{(int)span.TotalHours:D2}:" +
            $"{span.Minutes:D2}:" +
            $"{span.Seconds:D2}";
    }


    // ==============================
    // 恢复主窗口
    //
    // 托盘菜单、托盘双击、以及第二个实例发来的
    // 唤醒请求都会走到这里
    // ==============================
    public void RestoreFromTray()
    {
        // 窗口已经关闭（正在退出）就不能再显示
        if (_closed) return;

        Show();

        if (WindowState ==
            WindowState.Minimized)
        {
            WindowState =
                WindowState.Normal;
        }

        Activate();

        Topmost = true;
        Topmost = false;

        Focus();
    }


    // ==============================
    // 点击右上角 X
    //
    // 只是隐藏窗口，追踪服务继续在后台跑
    // ==============================
    protected override void OnClosing(
        CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;

            Hide();

            _trayService.ShowTrayTipOnce();

            return;
        }

        base.OnClosing(e);
    }


    // ==============================
    // 托盘菜单 → 退出
    //
    // 真正结束程序。追踪服务的停止和
    // 当前会话的落库由 App.OnExit 统一处理，
    // 这里不重复释放。
    // ==============================
    public void RequestExit()
    {
        // 防止重复点击"退出"
        if (_reallyExit) return;

        _reallyExit = true;

        // 先摘掉托盘图标，再关窗口。
        //
        // 顺序很重要：窗口关闭后到进程退出之间，
        // 托盘图标仍然可点，一点就会触发已关闭窗口的 Show
        // 并抛异常，异常又会让后面的清理跑不到，
        // 结果就是图标残留。
        _trayService.Dispose();

        Close();

        System.Windows.Application
            .Current
            .Shutdown();
    }


    // ==============================
    // 窗口真正关闭
    // ==============================
    protected override void OnClosed(
        EventArgs e)
    {
        _closed = true;

        _sessionTracker.SessionChanged -=
            OnSessionChanged;

        base.OnClosed(e);
    }
}
