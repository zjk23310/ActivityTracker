using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ActivityTracker.Startup;

// SystemEvents 依赖 Windows 消息泵，因此在独立后台 STA 线程上订阅和分发。
// 所有对外回调只表示“发生了事件”；消费者负责把请求投递给自己的后台循环。
public sealed class PowerEventListener : IHostedService, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeAutomatic = 0x0012;

    private readonly ILogger<PowerEventListener> _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _powerBroadcastWindow;
    private bool _subscribed;

    public PowerEventListener(
        ILogger<PowerEventListener> logger)
    {
        _logger = logger;
    }

    public event Action? Suspended;
    public event Action? Resumed;
    public event Action? SessionLocked;
    public event Action? SessionUnlocked;
    public event Action? SessionEnding;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_thread is not null)
            return Task.CompletedTask;

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "ActivityTracker SystemEvents"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(3), cancellationToken))
        {
            _logger.LogWarning(
                "SystemEvents 监听线程未在限时内就绪，将依赖心跳断层检测。");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is not null &&
            !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    Unsubscribe();
                    dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Send);
                }));
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        return Task.CompletedTask;
    }

    private void RunMessageLoop()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            CreatePowerBroadcastWindow();
            Subscribe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SystemEvents 订阅失败，将完全依赖心跳断层检测。");
        }
        finally
        {
            _ready.Set();
        }

        try
        {
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SystemEvents 消息循环异常结束，将依赖心跳断层检测。");
        }
        finally
        {
            Unsubscribe();
        }
    }

    private void Subscribe()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
        _subscribed = true;
        _logger.LogInformation("SystemEvents 电源与会话监听已启动。");
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.SessionEnding -= OnSessionEnding;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取消 SystemEvents 订阅失败。");
            }
            finally
            {
                _subscribed = false;
            }
        }

        DisposePowerBroadcastWindow();
    }

    private void OnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        try
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    _logger.LogInformation("系统即将挂起。");
                    Suspended?.Invoke();
                    break;
                case PowerModes.Resume:
                    _logger.LogInformation("系统已恢复。");
                    Resumed?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理电源事件失败。");
        }
    }

    private void CreatePowerBroadcastWindow()
    {
        // PowerModes 没有 ResumeAutomatic，且 SystemEvents 会忽略对应的
        // PBT_APMRESUMEAUTOMATIC。创建不可见顶层窗口只补收这一条原始消息。
        var parameters = new HwndSourceParameters(
            "ActivityTracker Power Broadcast Listener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };

        _powerBroadcastWindow = new HwndSource(parameters);
        _powerBroadcastWindow.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmPowerBroadcast ||
            wParam.ToInt32() != PbtApmResumeAutomatic)
        {
            return IntPtr.Zero;
        }

        try
        {
            _logger.LogInformation("系统已自动恢复。");
            Resumed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理系统自动恢复事件失败。");
        }

        handled = true;
        return new IntPtr(1);
    }

    private void DisposePowerBroadcastWindow()
    {
        var window = _powerBroadcastWindow;
        if (window is null)
            return;

        window.RemoveHook(OnWindowMessage);
        window.Dispose();
        _powerBroadcastWindow = null;
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs e)
    {
        try
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                    _logger.LogInformation("Windows 会话已锁定或断开。");
                    SessionLocked?.Invoke();
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                    _logger.LogInformation("Windows 会话已解锁或连接。");
                    SessionUnlocked?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 Windows 会话切换事件失败。");
        }
    }

    private void OnSessionEnding(
        object sender,
        SessionEndingEventArgs e)
    {
        try
        {
            _logger.LogInformation("Windows 会话即将结束。");
            SessionEnding?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 Windows 会话结束事件失败。");
        }
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        _ready.Dispose();
    }
}
