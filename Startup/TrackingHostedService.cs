using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ActivityTracker.Services;

namespace ActivityTracker.Startup;

// 把 SessionTracker 的启停挂到 Host 的生命周期上。
//
// 以前是 MainWindow.Loaded 里 Start、OnClosed 里 Dispose，
// 追踪的生死绑在窗口上。现在由 Host 控制：
// 进程一起来就开始追踪，即使主窗口没显示；
// 真正退出时 StopAsync 会结束当前会话并落库。
internal sealed class TrackingHostedService : IHostedService
{
    private readonly SessionTracker _tracker;
    private readonly ILogger<TrackingHostedService> _logger;

    public TrackingHostedService(
        SessionTracker tracker,
        ILogger<TrackingHostedService> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public Task StartAsync(
        CancellationToken cancellationToken)//IHostedService 规定 StopAsync 必须接收它。cancellationToken一个“取消通知令牌”，用来让异步/后台任务知道外面要求它停止了。
    {
        _logger.LogInformation("正在启动活动追踪服务。");
        _tracker.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("正在停止活动追踪服务。");
        // Dispose 内部会先结束当前会话段并写入数据库
        _tracker.Dispose();
        _logger.LogInformation("活动追踪服务已停止。");
        return Task.CompletedTask;
    }
}
