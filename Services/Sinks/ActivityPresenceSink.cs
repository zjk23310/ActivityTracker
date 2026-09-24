using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services.Sinks;

public sealed class ActivityPresenceSink :
    IHostedService,
    IPresenceStateProvider,
    IDisposable
{
    private readonly IActivityChangeBus _bus;
    private readonly IPresencePublisher _publisher;
    private readonly ILogger<ActivityPresenceSink> _logger;
    private IDisposable? _subscription;
    private ActivityChange? _current;

    public ActivityPresenceSink(
        IActivityChangeBus bus,
        IPresencePublisher publisher,
        ILogger<ActivityPresenceSink> logger)
    {
        _bus = bus;
        _publisher = publisher;
        _logger = logger;
    }

    public ActivityChange? Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(
            "Presence",
            ActivitySubscriptionOptions.LatestOnly,
            HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async ValueTask HandleAsync(
        ActivityChange change,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _current, change);

        try
        {
            await _publisher.PublishAsync(change, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Presence 发布失败。Kind={Kind}，AppId={AppId}。",
                change.Kind,
                change.Target.AppId);
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
