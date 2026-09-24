using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

public interface IPresencePublisher
{
    ValueTask PublishAsync(
        ActivityChange change,
        CancellationToken cancellationToken);
}

public interface IPresenceStateProvider
{
    ActivityChange? Current { get; }
}

public sealed class LoggingPresencePublisher : IPresencePublisher
{
    private readonly ILogger<LoggingPresencePublisher> _logger;

    public LoggingPresencePublisher(
        ILogger<LoggingPresencePublisher> logger)
    {
        _logger = logger;
    }

    public ValueTask PublishAsync(
        ActivityChange change,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Presence 状态更新：Kind={Kind}，AppId={AppId}，AppName={AppName}，IsIdle={IsIdle}。",
            change.Kind,
            change.Target.AppId,
            change.Target.AppName,
            change.Target.IsIdle);
        return ValueTask.CompletedTask;
    }
}
