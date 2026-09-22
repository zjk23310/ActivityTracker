using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using Microsoft.Extensions.Logging;

namespace ActivityTracker.Logging;

public sealed class RollingFileLoggerProvider :
    ILoggerProvider,
    ISupportExternalScope
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly int _retentionDays;
    private readonly long _maxFileSizeBytes;

    private IExternalScopeProvider _scopeProvider =
        new LoggerExternalScopeProvider();

    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _disposed;

    public RollingFileLoggerProvider(
        string directory,
        LogLevel minimumLevel,
        int retentionDays,
        int maxFileSizeMb)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
        _maxFileSizeBytes =
            Math.Clamp(maxFileSizeMb, 1, 100) * 1024L * 1024L;

        Directory.CreateDirectory(_directory);
        DeleteExpiredLogs();
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new RollingFileLogger(this, categoryName);
    }

    public void SetScopeProvider(
        IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    internal IExternalScopeProvider ScopeProvider =>
        _scopeProvider;

    internal bool IsEnabled(LogLevel level)
    {
        return !_disposed &&
               level >= _minimumLevel &&
               level != LogLevel.None;
    }

    internal void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (!IsEnabled(level))
            return;

        lock (_sync)
        {
            if (_disposed)
                return;

            try
            {
                var timestamp = DateTimeOffset.Now;
                EnsureWriter(timestamp);

                var line = FormatLine(
                    timestamp,
                    category,
                    level,
                    eventId,
                    message,
                    exception);

                var byteCount =
                    Encoding.UTF8.GetByteCount(line + Environment.NewLine);

                if (_writer is not null &&
                    _writer.BaseStream.Length + byteCount >
                    _maxFileSizeBytes)
                {
                    OpenWriter(DateOnly.FromDateTime(DateTime.Now));
                }

                _writer?.WriteLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Logging] 无法写入日志：{ex}");
            }
        }
    }

    private void EnsureWriter(DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);

        if (_writer is null || date != _currentDate)
            OpenWriter(date);
    }

    private void OpenWriter(DateOnly date)
    {
        _writer?.Dispose();
        _writer = null;
        _currentDate = date;

        var sequence = 0;
        string path;

        while (true)
        {
            var suffix = sequence == 0
                ? ""
                : $"-{sequence:D3}";

            path = Path.Combine(
                _directory,
                $"activitytracker-{date:yyyyMMdd}{suffix}.log");

            if (!File.Exists(path) ||
                new FileInfo(path).Length < _maxFileSizeBytes)
            {
                break;
            }

            sequence++;
        }

        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        _writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        DeleteExpiredLogs();
    }

    private void DeleteExpiredLogs()
    {
        try
        {
            var cutoff =
                DateTime.UtcNow.AddDays(-_retentionDays);

            foreach (var path in Directory.EnumerateFiles(
                         _directory,
                         "activitytracker-*.log",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[Logging] 无法清理旧日志：{ex}");
        }
    }

    private static string FormatLine(
        DateTimeOffset timestamp,
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        var levelText = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "NON"
        };

        var eventText = eventId.Id == 0
            ? ""
            : $" [{eventId.Id}]";

        var result =
            $"{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} " +
            $"[{levelText}] {category}{eventText} {message}";

        if (exception is not null)
            result += Environment.NewLine + exception;

        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(
            RollingFileLoggerProvider provider,
            string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return _provider.ScopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return _provider.IsEnabled(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            _provider.Write(
                _category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception);
        }
    }
}
