using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Startup;

// 监听命名管道，接收"把主窗口显示出来"的请求。
//
// 用户第二次启动程序时，新进程连上这个管道，
// 当前实例就把窗口从托盘里弹出来，新进程自己退出。
internal sealed class WakeListenerService : IHostedService//实现IHostedService接口，表示这是一个托管服务
{
    private readonly string _pipeName;
    private readonly ILogger<WakeListenerService> _logger;

    private CancellationTokenSource? _cts;//取消令牌源，用于取消监听循环
    private Task? _listenLoop;//Task表示监听循环的任务

    // 收到唤醒请求时触发。
    // 注意：这个事件是在后台线程上触发的。
    public event Action? WakeRequested;//唤醒请求事件

    public WakeListenerService(
        string pipeName,
        ILogger<WakeListenerService> logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public Task StartAsync(
        CancellationToken cancellationToken)//CancellationToken表示取消操作的通知，允许在操作执行期间取消操作
    {
        _logger.LogInformation("正在启动单实例唤醒监听。");
        _cts = new CancellationTokenSource();
        _listenLoop = Task.Run(
            () => ListenLoopAsync(_cts.Token));

        return Task.CompletedTask;
    }

    public async Task StopAsync(
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("正在停止单实例唤醒监听。");
        _cts?.Cancel();

        if (_listenLoop is not null)
        {
            // 只等有限时间，绝不无限期等下去。
            //
            // 取消 token 之后，循环里那个 `using` 会去释放管道，
            // 而 Dispose 有可能卡在刚被取消的挂起 I/O 上不返回。
            // 如果在这里 await _listenLoop 而不设上限，
            // 整个退出流程就会永远停住，进程关不掉、托盘图标残留。
            //
            // 监听线程留在那里无所谓，进程本来就要退出了。
            await Task.WhenAny(
                _listenLoop,
                Task.Delay(1500));
        }

        try
        {
            _cts?.Dispose();
        }
        catch
        {
            // 循环可能还在引用这个 token，忽略即可
        }

        _cts = null;
        _logger.LogInformation("单实例唤醒监听已停止。");
    }

    // 监听循环：不断创建管道，等待连接。
    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)//IsCancellationRequested是CancellationToken的一个属性，表示是否请求取消操作
        {
            try
            {
                using var server = new NamedPipeServerStream(//各个属性分别是：管道名，方向，最大连接数，传输模式，异步选项,using表示在using块结束时自动释放资源
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);//异步IO

                await server.WaitForConnectionAsync(token);//等待客户端连接，传入取消令牌
            }
            catch (OperationCanceledException)//OperationCanceledException表示操作被取消的异常
                when (token.IsCancellationRequested)
            {
                // 只有真的收到取消才退出
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "命名管道监听失败，稍后重试。");

                // 管道名被占用之类的异常，
                // 稍等一下再重建，避免忙等把 CPU 跑满
                try
                {
                    await Task.Delay(500, token);//延迟500毫秒，传入取消令牌，Delay方法会返回一个Task，表示延迟完成的异步操作
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // 唤醒订阅者。
            //
            // 这里必须把异常全部吃掉：订阅者里会调用
            // Dispatcher.Invoke，而它会抛 TaskCanceledException
            // （OperationCanceledException 的子类）。
            // 如果让它冒出去，监听循环就会永久退出，
            // 唤醒功能以后再也不会生效，而且没有任何迹象。
            try
            {
                _logger.LogDebug("收到另一个实例的窗口唤醒请求。");
                WakeRequested?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "处理窗口唤醒请求时发生异常。");
            }
        }
    }
}
