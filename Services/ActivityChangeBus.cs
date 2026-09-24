using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

/// <summary>
/// 在追踪器与独立消费者之间传递不可变活动快照。
/// </summary>
/// <remarks>
/// 线程契约：<see cref="Publish"/> 只由 tracker worker 单线程调用。发布路径只分配
/// 序号并对入口通道执行 <c>TryWrite</c>；它不等待、不调用订阅者、不获取锁且不向
/// 调用者抛异常，因此耗时不随订阅者数量或速度变化。扇出和每个 handler 都运行在
/// 各自后台消费任务上；UI handler 必须再通过 IUiDispatcher 投递到 UI 线程。
/// ForegroundWindowTracker 的 hook 注册线程不由此类型改变。
/// </remarks>
public sealed class ActivityChangeBus :
    IActivityChangeBus,
    IDisposable
{
    private readonly Channel<ActivityChange> _ingress =
        Channel.CreateUnbounded<ActivityChange>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
    private readonly ILogger<ActivityChangeBus> _logger;
    private readonly object _subscriptionsSync = new();
    private readonly Dictionary<long, Subscription> _subscriptions = new();
    private readonly Task _dispatcher;
    private long _sequence;
    private long _subscriptionId;
    private int _draining;
    private int _disposed;

    public ActivityChangeBus(
        ILogger<ActivityChangeBus> logger)
    {
        _logger = logger;
        _dispatcher = Task.Run(DispatchAsync);
    }

    public BusDrainReport? LastDrainReport { get; private set; }

    /// <summary>
    /// 以恒定发布路径写入活动事件；此方法不等待、不调用订阅者、不获取锁且不抛异常。
    /// </summary>
    public void Publish(ActivityChange change)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _ingress.Writer.TryWrite(change with { Sequence = sequence });
    }

    public IDisposable Subscribe(
        string name,
        ActivitySubscriptionOptions options,
        Func<ActivityChange, CancellationToken, ValueTask> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        if (Volatile.Read(ref _draining) != 0)
        {
            throw new InvalidOperationException(
                "活动事件总线已经开始排空，不能再添加订阅者。");
        }

        var id = Interlocked.Increment(ref _subscriptionId);
        var subscription = new Subscription(
            id,
            name,
            options,
            handler,
            _logger,
            RemoveSubscription);

        lock (_subscriptionsSync)
        {
            if (_draining != 0)
            {
                subscription.Dispose();
                throw new InvalidOperationException(
                    "活动事件总线已经开始排空，不能再添加订阅者。");
            }

            _subscriptions.Add(id, subscription);
        }

        return subscription;
    }

    public async Task<BusDrainReport> DrainAsync(
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0)
            _ingress.Writer.TryComplete();

        var deadline = DateTime.UtcNow + grace;
        await WaitUntilAsync(
            _dispatcher,
            Remaining(deadline),
            cancellationToken)
            .ConfigureAwait(false);
        var dispatcherDrained = _dispatcher.IsCompleted;

        Subscription[] subscriptions;
        lock (_subscriptionsSync)
            subscriptions = _subscriptions.Values.ToArray();

        if (dispatcherDrained)
        {
            foreach (var subscription in subscriptions)
                subscription.Complete();
        }

        await Task.WhenAll(
            subscriptions.Select(subscription =>
                WaitUntilAsync(
                    subscription.Completion,
                    Remaining(deadline),
                    cancellationToken)))
            .ConfigureAwait(false);

        var results = subscriptions
            .Select(subscription =>
                new BusSubscriberDrainResult(
                    subscription.Name,
                    subscription.PendingCount,
                    dispatcherDrained && subscription.Completion.IsCompleted))
            .ToArray();

        LastDrainReport = new BusDrainReport(results);
        return LastDrainReport;
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var change in
                _ingress.Reader.ReadAllAsync())
            {
                Subscription[] subscriptions;
                lock (_subscriptionsSync)
                    subscriptions = _subscriptions.Values.ToArray();

                foreach (var subscription in subscriptions)
                    subscription.TryWrite(change);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "活动事件总线扇出循环异常结束。");
        }
        finally
        {
            Subscription[] subscriptions;
            lock (_subscriptionsSync)
                subscriptions = _subscriptions.Values.ToArray();

            foreach (var subscription in subscriptions)
                subscription.Complete();
        }
    }

    private void RemoveSubscription(long id)
    {
        lock (_subscriptionsSync)
            _subscriptions.Remove(id);
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.Zero;
    }

    private static async Task WaitUntilAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        try
        {
            await task.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly long _id;
        private readonly ActivitySubscriptionOptions _options;
        private readonly Func<ActivityChange, CancellationToken, ValueTask> _handler;
        private readonly ILogger _logger;
        private readonly Action<long> _remove;
        private readonly Channel<ActivityChange> _channel;
        private readonly CancellationTokenSource _cancellation = new();
        private int _pendingCount;
        private int _warningActive;
        private int _disposed;

        public Subscription(
            long id,
            string name,
            ActivitySubscriptionOptions options,
            Func<ActivityChange, CancellationToken, ValueTask> handler,
            ILogger logger,
            Action<long> remove)
        {
            _id = id;
            Name = name;
            _options = options;
            _handler = handler;
            _logger = logger;
            _remove = remove;
            _channel = options.DropOldest
                ? Channel.CreateBounded<ActivityChange>(
                    new BoundedChannelOptions(
                        Math.Max(1, options.Capacity))
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    })
                : Channel.CreateUnbounded<ActivityChange>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    });
            Completion = Task.Run(ConsumeAsync);
        }

        public string Name { get; }
        public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));
        public Task Completion { get; }

        public void TryWrite(ActivityChange change)
        {
            if (!_channel.Writer.TryWrite(change))
                return;

            var pending = _options.DropOldest
                ? Interlocked.Exchange(ref _pendingCount, 1)
                : Interlocked.Increment(ref _pendingCount);

            if (!_options.DropOldest &&
                pending > _options.WarningThreshold &&
                Interlocked.Exchange(ref _warningActive, 1) == 0)
            {
                _logger.LogWarning(
                    "活动事件订阅者 {SubscriberName} 待处理深度已达 {PendingCount}。",
                    Name,
                    pending);
            }
        }

        public void Complete() =>
            _channel.Writer.TryComplete();

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var change in
                    _channel.Reader.ReadAllAsync(_cancellation.Token))
                {
                    if (_options.DropOldest)
                        Interlocked.Exchange(ref _pendingCount, 0);
                    else
                        Interlocked.Decrement(ref _pendingCount);

                    try
                    {
                        await _handler(change, _cancellation.Token);
                    }
                    catch (OperationCanceledException)
                        when (_cancellation.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "活动事件订阅者 {SubscriberName} 处理事件失败。",
                            Name);
                    }

                    if (!_options.DropOldest &&
                        PendingCount <= _options.WarningThreshold / 2)
                    {
                        Interlocked.Exchange(ref _warningActive, 0);
                    }
                }
            }
            catch (OperationCanceledException)
                when (_cancellation.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _remove(_id);
            Complete();
            Completion.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }
    }
}
