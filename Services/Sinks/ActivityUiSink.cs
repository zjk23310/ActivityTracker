using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

namespace ActivityTracker.Services.Sinks;

public sealed class ActivityUiSink : IHostedService, IDisposable
{
    private readonly IActivityChangeBus _bus;
    private readonly UiActivityNotifier _notifier;
    private IDisposable? _subscription;

    public ActivityUiSink(
        IActivityChangeBus bus,
        UiActivityNotifier notifier)
    {
        _bus = bus;
        _notifier = notifier;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(
            "UI",
            ActivitySubscriptionOptions.LatestOnly,
            _notifier.HandleAsync);
        return Task.CompletedTask;
    }

    // DB sink 在停止顺序的最后统一排空总线；这里保留订阅，避免丢失 tracker 的尾事件。
    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
