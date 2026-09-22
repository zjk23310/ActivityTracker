using System;
using System.Threading;
using ActivityTracker.Data;
using ActivityTracker.Models;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Services;

public sealed class SessionTracker : IDisposable//这个对象用完后需要主动清理资源
{
    private readonly ActivityRepository _repository;
    private readonly AppIdentityResolver _appIdentityResolver;
    private readonly ForegroundWindowTracker _foregroundTracker;//前台窗口跟踪器
    private readonly System.Threading.Timer _idleTimer;//定时器，定时检查是否空闲
    private readonly object _sync = new();//锁对象，保证线程安全
    private readonly TimeSpan _idleThreshold;//空闲阈值
    private readonly ILogger<SessionTracker> _logger;

    private WindowInfo? _currentWindow;
    private AppIdentity? _currentIdentity;
    private DateTime _segmentStart;//当前会话段的开始时间
    private bool _isIdle;
    private bool _started;

    public event Action? SessionChanged;

    public SessionTracker(
        ActivityRepository repository,
        AppIdentityResolver appIdentityResolver,
        TimeSpan? idleThreshold,
        ILogger<SessionTracker> logger)//构造函数，传入数据库仓库和空闲阈值
    {
        _repository = repository;
        _appIdentityResolver = appIdentityResolver;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(5);//默认空闲阈值为5分钟,FromMinutes是一个静态方法，返回一个TimeSpan对象，表示指定的分钟数
        _logger = logger;
        _foregroundTracker = new ForegroundWindowTracker();
        _foregroundTracker.ForegroundChanged += OnForegroundChanged;//订阅前台窗口变化事件
        _idleTimer = new System.Threading.Timer(CheckIdle, null, Timeout.Infinite, Timeout.Infinite);//初始化定时器，第一次不启动，后续由Start方法启动
    }
    // 开始跟踪会话
    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            _segmentStart = DateTime.Now;
            _foregroundTracker.Start();
            _idleTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));//每2秒检查一次是否空闲
        }

        _logger.LogInformation(
            "活动追踪已启动，空闲阈值为 {IdleMinutes} 分钟。",
            _idleThreshold.TotalMinutes);
    }

    //
    private void OnForegroundChanged(WindowInfo info)
    {
        // 趁进程仍存活时解析包身份；Resolver 内部保证失败时安全 fallback。
        var identity =
            _appIdentityResolver.Resolve(info);

        lock (_sync)
        {
            if (!_started) return;

            if (_isIdle)
            {
                _currentWindow = info;
                _currentIdentity = identity;
                return;
            }

            if (_currentWindow == info)
                return;

            CloseCurrentSegment(DateTime.Now);
            _currentWindow = info;
            _currentIdentity = identity;
            _segmentStart = DateTime.Now;
        }

        SessionChanged?.Invoke();
    }

    // 检查是否空闲
    private void CheckIdle(object? state)
    {
        bool becameIdle;

        lock (_sync)
        {
            if (!_started) return;
            var now = DateTime.Now;
            var shouldBeIdle = IdleDetector.GetIdleTime() >= _idleThreshold;

            if (shouldBeIdle == _isIdle)
                return;

            CloseCurrentSegment(now);
            _isIdle = shouldBeIdle;
            becameIdle = shouldBeIdle;
            _segmentStart = now;
        }

        _logger.LogInformation(
            becameIdle
                ? "用户状态切换为空闲。"
                : "用户状态恢复为活跃。");

        SessionChanged?.Invoke();//触发会话变化事件
    }

    // 关闭当前会话段，并将其保存到数据库
    private void CloseCurrentSegment(DateTime end)
    {
        if (_segmentStart == default || end <= _segmentStart)
            return;

        var duration = (int)(end - _segmentStart).TotalSeconds;
        if (duration <= 0)
            return;

        if (_isIdle)
        {
            Persist(new ActivitySession
            {
                ProcessName = "IDLE",
                WindowTitle = "用户空闲",
                ExecutablePath = "",
                AppId = AppIdentity.Idle.AppId,
                AppName = AppIdentity.Idle.AppName,
                StartTime = _segmentStart,
                EndTime = end,
                DurationSeconds = duration,
                IsIdle = true
            });
        }
        else if (_currentWindow is not null)
        {
            var identity =
                _currentIdentity ??
                _appIdentityResolver.Resolve(_currentWindow);

            Persist(new ActivitySession
            {
                ProcessName = _currentWindow.ProcessName,
                WindowTitle = _currentWindow.WindowTitle,
                ExecutablePath = _currentWindow.ExecutablePath,
                AppId = identity.AppId,
                AppName = identity.AppName,
                StartTime = _segmentStart,
                EndTime = end,
                DurationSeconds = duration,
                IsIdle = false
            });
        }
    }

    private void Persist(ActivitySession session)
    {
        try
        {
            _repository.Insert(session);

            _logger.LogDebug(
                "已保存活动片段：{ProcessName}，{DurationSeconds} 秒，Idle={IsIdle}。",
                session.ProcessName,
                session.DurationSeconds,
                session.IsIdle);
        }
        catch (Exception ex)
        {
            // 单次写入失败不应让常驻追踪循环永久停止。
            // 不记录窗口标题，避免日志泄露文档名或网页标题。
            _logger.LogError(
                ex,
                "保存活动片段失败：{ProcessName}，{DurationSeconds} 秒，Idle={IsIdle}。",
                session.ProcessName,
                session.DurationSeconds,
                session.IsIdle);
        }
    }

    //获取当前快照
    public ActivitySession? GetCurrentSnapshot()
    {
        lock (_sync)//锁
        {
            if (!_started || _segmentStart == default)
                return null;

            var now = DateTime.Now;

            if (now <= _segmentStart)
                return null;

            var duration =//持续事件
                (int)(now - _segmentStart).TotalSeconds;

            if (duration <= 0)
                return null;

            // 当前处于空闲状态
            if (_isIdle)
            {
                return new ActivitySession
                {
                    ProcessName = "IDLE",
                    WindowTitle = "用户空闲",
                    ExecutablePath = "",
                    AppId = AppIdentity.Idle.AppId,
                    AppName = AppIdentity.Idle.AppName,
                    StartTime = _segmentStart,
                    EndTime = now,
                    DurationSeconds = duration,
                    IsIdle = true
                };
            }

            // 当前还没有检测到前台窗口
            if (_currentWindow is null)
                return null;

            var identity =
                _currentIdentity ??
                _appIdentityResolver.Resolve(_currentWindow);

            return new ActivitySession
            {
                ProcessName = _currentWindow.ProcessName,
                WindowTitle = _currentWindow.WindowTitle,
                ExecutablePath = _currentWindow.ExecutablePath,
                AppId = identity.AppId,
                AppName = identity.AppName,
                StartTime = _segmentStart,
                EndTime = now,
                DurationSeconds = duration,
                IsIdle = false
            };
        }
    }

    // 释放资源
    public void Dispose()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            CloseCurrentSegment(DateTime.Now);
        }

        _idleTimer.Dispose();
        _foregroundTracker.Dispose();

        _logger.LogInformation("活动追踪已释放。");
    }
}
