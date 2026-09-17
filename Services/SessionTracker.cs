using System;
using System.Threading;
using ActivityTracker.Data;
using ActivityTracker.Models;

namespace ActivityTracker.Services;

public sealed class SessionTracker : IDisposable
{
    private readonly ActivityRepository _repository;
    private readonly ForegroundWindowTracker _foregroundTracker;
    private readonly System.Threading.Timer _idleTimer;
    private readonly object _sync = new();
    private readonly TimeSpan _idleThreshold;

    private WindowInfo? _currentWindow;
    private DateTime _segmentStart;
    private bool _isIdle;
    private bool _started;

    public event Action? SessionChanged;

    public SessionTracker(ActivityRepository repository, TimeSpan? idleThreshold = null)
    {
        _repository = repository;
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(5);
        _foregroundTracker = new ForegroundWindowTracker();
        _foregroundTracker.ForegroundChanged += OnForegroundChanged;
        _idleTimer = new System.Threading.Timer(CheckIdle, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
            _segmentStart = DateTime.Now;
            _foregroundTracker.Start();
            _idleTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
    }

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

        SessionChanged?.Invoke();
    }

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
