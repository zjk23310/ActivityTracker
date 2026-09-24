using System;
using System.Threading;
using System.Threading.Tasks;

using ActivityTracker.Configuration;
using ActivityTracker.Startup;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

public sealed class UiActivityNotifier : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly SettingsService _settingsService;
    private readonly ILogger<UiActivityNotifier> _logger;
    private readonly object _sync = new();
    private DateTimeOffset _lastNotificationUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _scheduled;
    private bool _dirty;
    private int _disposed;

    public UiActivityNotifier(
        IUiDispatcher dispatcher,
        SettingsService settingsService,
        ILogger<UiActivityNotifier> logger)
    {
        _dispatcher = dispatcher;
        _settingsService = settingsService;
        _logger = logger;
    }

    public event Action? SessionChanged;

    public async ValueTask HandleAsync(
        ActivityChange change,
        CancellationToken cancellationToken)
    {
        if (change.Kind == ActivityChangeKind.Updated ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var fireNow = false;
        TimeSpan delay = TimeSpan.Zero;
        CancellationToken token = default;

        lock (_sync)
        {
            var interval = TimeSpan.FromMilliseconds(
                _settingsService.Current.Tracking.UiRefreshIntervalMs);
            var elapsed = DateTimeOffset.UtcNow - _lastNotificationUtc;

            if (_scheduled is null && elapsed >= interval)
            {
                _lastNotificationUtc = DateTimeOffset.UtcNow;
                _dirty = false;
                fireNow = true;
            }
            else
            {
                _dirty = true;
                if (_scheduled is null)
                {
                    _scheduled = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    token = _scheduled.Token;
                    delay = elapsed >= interval
                        ? TimeSpan.Zero
                        : interval - elapsed;
                }
            }
        }

        if (fireNow)
            PostNotification();
        else if (token.CanBeCanceled)
            await FlushAfterDelayAsync(delay, token);
    }

    private async Task FlushAfterDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var shouldNotify = false;
            lock (_sync)
            {
                if (_dirty && Volatile.Read(ref _disposed) == 0)
                {
                    _dirty = false;
                    _lastNotificationUtc = DateTimeOffset.UtcNow;
                    shouldNotify = true;
                }

                _scheduled?.Dispose();
                _scheduled = null;
            }

            if (shouldNotify)
                PostNotification();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "调度 UI 活动刷新失败。");
        }
    }

    private void PostNotification()
    {
        _dispatcher.Post(() =>
        {
            try
            {
                SessionChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UI 活动刷新订阅者执行失败。");
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_sync)
        {
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            _scheduled = null;
            _dirty = false;
        }
    }
}
