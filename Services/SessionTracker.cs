using System;
using System.Threading;
using ActivityTracker.Data;
using ActivityTracker.Models;

namespace ActivityTracker.Services;

public sealed class SessionTracker : IDisposable
{
    private readonly ActivityRepository _repository;
    private readonly ForegroundWindowTracker _foregroundTracker;//前台窗口跟踪器
    private readonly System.Threading.Timer _idleTimer;//定时器，定时检查是否空闲
    private readonly object _sync = new();//锁对象，保证线程安全
    private readonly TimeSpan _idleThreshold;

    private WindowInfo? _currentWindow;
    private DateTime _segmentStart;//当前会话段的开始时间
    private bool _isIdle;
    private bool _started;

    public event Action? SessionChanged;

    public SessionTracker(ActivityRepository repository, TimeSpan? idleThreshold = null)//构造函数，传入数据库仓库和空闲阈值
    {
        _repository = repository;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(5);//默认空闲阈值为5分钟,FromMinutes是一个静态方法，返回一个TimeSpan对象，表示指定的分钟数
        _foregroundTracker = new ForegroundWindowTracker();
        _foregroundTracker.ForegroundChanged += OnForegroundChanged;
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
    }

    //
    private void OnForegroundChanged(WindowInfo info)
    {
        lock (_sync)
        {
            if (!_started) return;

            if (_isIdle)
            {
                _currentWindow = info;
                return;
            }

            if (_currentWindow == info)
                return;

            CloseCurrentSegment(DateTime.Now);
            _currentWindow = info;
            _segmentStart = DateTime.Now;
        }

        SessionChanged?.Invoke();
    }

    // 检查是否空闲
    private void CheckIdle(object? state)
    {
        lock (_sync)
        {
            if (!_started) return;
            var now = DateTime.Now;
            var shouldBeIdle = IdleDetector.GetIdleTime() >= _idleThreshold;

            if (shouldBeIdle == _isIdle)
                return;

            CloseCurrentSegment(now);
            _isIdle = shouldBeIdle;
            _segmentStart = now;
        }

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
            _repository.Insert(new ActivitySession
            {
                ProcessName = "IDLE",
                WindowTitle = "用户空闲",
                ExecutablePath = "",
                StartTime = _segmentStart,
                EndTime = end,
                DurationSeconds = duration,
                IsIdle = true
            });
        }
        else if (_currentWindow is not null)
        {
            _repository.Insert(new ActivitySession
            {
                ProcessName = _currentWindow.ProcessName,
                WindowTitle = _currentWindow.WindowTitle,
                ExecutablePath = _currentWindow.ExecutablePath,
                StartTime = _segmentStart,
                EndTime = end,
                DurationSeconds = duration,
                IsIdle = false
            });
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
                    StartTime = _segmentStart,
                    EndTime = now,
                    DurationSeconds = duration,
                    IsIdle = true
                };
            }

            // 当前还没有检测到前台窗口
            if (_currentWindow is null)
                return null;

            return new ActivitySession
            {
                ProcessName = _currentWindow.ProcessName,
                WindowTitle = _currentWindow.WindowTitle,
                ExecutablePath = _currentWindow.ExecutablePath,
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
    }
}
