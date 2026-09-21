using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ActivityTracker.Services;
using ActivityTracker.Startup;

namespace ActivityTracker;

public partial class App : System.Windows.Application
{
    private IHost? _host;//hosting
    private SingleInstanceGuard? _guard;//防止启动多个
    private MainWindow? _mainWindow;
    private int _shutdownWatchdogStarted;//退出保险

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ==============================
        // 单实例检查
        //
        // 必须放在最前面：要在打开数据库、
        // 构造追踪器之前就退出，
        // 否则第二个实例会往同一个库里重复写
        // ==============================
        _guard = new SingleInstanceGuard();

        if (!_guard.IsFirstInstance)
        {
            // 已经有实例在跑：让它把窗口弹出来，然后自己退出
            var woken =
                SingleInstanceGuard.TryWakeExistingInstance(
                    _guard.PipeName);

            if (!woken)//未唤醒就提示
            {
                // 对方刚启动、管道还没就绪时会走到这里
                // （冷启动时它可能正在做数据库迁移）。
                // 静默退出会让用户以为程序没反应，所以给个提示。
                System.Windows.MessageBox.Show(
                    "ActivityTracker 已经在运行，请在系统托盘中查看。",
                    "提示",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        AppPaths.EnsureDataDirectory();

        // ==============================
        // 建立服务容器
        //
        // 内容根必须指定成程序所在目录。
        // 默认取当前工作目录，而从快捷方式启动时
        // 那可能是 System32，会导致配置文件定位错误。
        // ==============================
        var builder = Host.CreateApplicationBuilder(//创建管理容器
            new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory
            });

        builder.Services.AddActivityTracker(
            _guard.PipeName);

        _host = builder.Build();//注册完成，正式把 Host 建出来。

        // ==============================
        // 接线
        //
        // 托盘和管道都只抛"请求"，不知道窗口长什么样；
        // 真正操作窗口的动作在这里接上，
        // 这样 TrayService 就不用反过来依赖 MainWindow。
        // ==============================
        _mainWindow =
            _host.Services.GetRequiredService<MainWindow>();//Hosting 就会查看 MainWindow 的构造函数需要什么，并自动解决依赖

        var tray = _host.Services
            .GetRequiredService<TrayService>();

        var wake = _host.Services
            .GetRequiredService<WakeListenerService>();

        // NotifyIcon 使用的是 WinForms 消息机制。
        // 不要在托盘菜单的 Click 回调内部直接隐藏、释放托盘并关闭 WPF；
        // 先异步投递给 WPF Dispatcher，让菜单事件正常返回，
        // 否则 WinForms 的菜单消息循环可能残留，进程无法彻底退出。
        tray.ShowRequested += OnTrayShowRequested;//事件订阅+=连接事件
        tray.ExitRequested += OnTrayExitRequested;

        wake.WakeRequested += OnWakeRequested;

        // 事件全部接好以后再启动后台服务。
        // 否则管道可能先收到第二个实例的唤醒请求，
        // 但 WakeRequested 还没有订阅者，导致请求丢失。
        // 追踪器仍然会在窗口显示之前启动。
        _host.Start();//后台追踪、唤醒监听等需要在这里开始运行。

        _mainWindow.Show();
    }

    // 管道监听在后台线程上触发，切回 UI 线程再动窗口
    //这种绑定事件可以降低耦合，TrayService 不需要知道 MainWindow 的存在。
    private void OnWakeRequested()
    {
        try
        {
            // 这里用 InvokeAsync 而不是 Invoke。
            //
            // Invoke 会阻塞管道线程，一直等到 UI 线程处理完；
            // 而退出时 UI 线程正阻塞在 OnExit 里等待 StopAsync，
            // 两者相遇就是死锁。
            Dispatcher.InvokeAsync(//让 WPF 的 UI 线程去执行窗口操作。
                () => _mainWindow?.RestoreFromTray());
        }
        catch
        {
            // 退出过程中调度器可能已经关闭，忽略即可
        }
    }

    private void OnTrayShowRequested()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.InvokeAsync(
            () => _mainWindow?.RestoreFromTray());
    }

    private void OnTrayExitRequested()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        StartShutdownWatchdog();

        Dispatcher.InvokeAsync(
            () => _mainWindow?.RequestExit());
    }

    private void StartShutdownWatchdog()
    {
        if (Interlocked.Exchange(
                ref _shutdownWatchdogStarted,
                1) != 0)
        {
            return;
        }

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));

            Debug.WriteLine(
                "[Shutdown] Cleanup exceeded 10 seconds; " +
                "forcing process termination.");

            Environment.Exit(0);//强制退出
        })
        {
            IsBackground = true,
            Name = "ActivityTracker shutdown watchdog"
        };

        watchdog.Start();
    }

    protected override void OnExit(ExitEventArgs e)//程序真正退出时执行
    {
        Debug.WriteLine("[关闭] App.OnExit 开始.");

        if (_host is not null)
        {
            var tray = _host.Services
                .GetService<TrayService>();

            if (tray is not null)
            {
                tray.ShowRequested -= OnTrayShowRequested;//取消订阅事件
                tray.ExitRequested -= OnTrayExitRequested;
            }

            var wake = _host.Services
                .GetService<WakeListenerService>();

            if (wake is not null)
                wake.WakeRequested -= OnWakeRequested;
        }

        // 这里必须同步等待。
        // StopAsync 会结束当前会话段并写入数据库，
        // 如果改成异步放走，进程可能在落库之前就退出了，
        // 最后一段活动记录会丢。
        if (_host is not null)
        {
            try
            {
                Debug.WriteLine("[关闭] Stopping Host.");

                _host.StopAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();

                Debug.WriteLine("[关闭] Host 停止.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[关闭] Host stop 失败: {ex}");

                // 退出阶段的异常不再往上抛，
                // 否则会盖掉正常的退出流程
            }

            Debug.WriteLine("[关闭] Disposing Host.");
            _host.Dispose();
            Debug.WriteLine("[关闭] Host disposed.");
            _host = null;
        }

        Debug.WriteLine("[关闭] Disposing single-instance guard.");
        _guard?.Dispose();
        _guard = null;

        base.OnExit(e);

        // 到这里，当前会话已经落库，Host、托盘、管道和 Mutex
        // 都完成了清理。显式结束进程，避免 WPF 与 WinForms
        // 混合消息循环在调试环境下残留一个无窗口进程。
        Debug.WriteLine(
            "[关闭] Cleanup completed; terminating process.");

        Environment.Exit(0);
    }
}
