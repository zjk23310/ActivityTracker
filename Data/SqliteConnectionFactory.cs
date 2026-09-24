using System;
using System.IO;
using System.Threading;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Data;

public readonly record struct DatabaseWriteResult<T>(
    bool Success,
    T Value);

// SQLite 连接的唯一创建入口。
// WAL + synchronous=NORMAL 能保证进程崩溃后的数据库一致性；若整个操作系统或硬件突然失效，
// 最后若干个已经提交的事务仍可能丢失。ActivityTracker 接受这个取舍，以换取常驻心跳写入时
// 更低的磁盘同步开销。
public sealed class SqliteConnectionFactory
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(900)
    };

    private readonly ILogger<SqliteConnectionFactory> _logger;
    private int _consecutiveWriteFailures;

    public SqliteConnectionFactory(
        string databasePath,
        ILogger<SqliteConnectionFactory> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
    }

    public string DatabasePath { get; }

    public int ConsecutiveWriteFailures =>
        Volatile.Read(ref _consecutiveWriteFailures);

    public void Initialize()
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(DatabasePath)!);

        using var connection = CreateAndOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SqlConstants.EnableWal;
        var mode = Convert.ToString(command.ExecuteScalar());

        if (!string.Equals(
                mode,
                "wal",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"无法启用 SQLite WAL 模式，当前模式为 {mode ?? "未知"}。");
        }
    }

    public SqliteConnection OpenConnection()
    {
        return CreateAndOpenConnection(
            DatabasePath,
            SqliteOpenMode.ReadWriteCreate,
            pooling: true,
            SqlConstants.ConfigureConnection);
    }

    public SqliteConnection OpenReadOnlyConnection(
        string? databasePath = null)
    {
        return CreateAndOpenConnection(
            databasePath ?? DatabasePath,
            SqliteOpenMode.ReadOnly,
            pooling: false,
            SqlConstants.ConfigureReadOnlyConnection);
    }

    public DatabaseWriteResult<T> ExecuteWrite<T>(
        string operationName,
        Func<SqliteConnection, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                var value = operation(connection);
                Interlocked.Exchange(
                    ref _consecutiveWriteFailures,
                    0);
                return new DatabaseWriteResult<T>(true, value);
            }
            catch (SqliteException ex)
                when (IsBusy(ex) && attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];
                _logger.LogWarning(
                    "SQLite 写入繁忙，操作 {OperationName} 将在 {DelayMs}ms 后重试（第 {Attempt} 次，错误码 {ErrorCode}）。",
                    operationName,
                    delay.TotalMilliseconds,
                    attempt + 1,
                    ex.SqliteErrorCode);
                Thread.Sleep(delay);
            }
            catch (SqliteException ex)
            {
                Interlocked.Increment(
                    ref _consecutiveWriteFailures);
                _logger.LogError(
                    ex,
                    "SQLite 写入操作 {OperationName} 最终失败，错误码 {ErrorCode}，扩展错误码 {ExtendedErrorCode}。",
                    operationName,
                    ex.SqliteErrorCode,
                    ex.SqliteExtendedErrorCode);
                return new DatabaseWriteResult<T>(false, default!);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(
                    ref _consecutiveWriteFailures);
                _logger.LogError(
                    ex,
                    "数据库写入操作 {OperationName} 最终失败。",
                    operationName);
                return new DatabaseWriteResult<T>(false, default!);
            }
        }
    }

    public bool Checkpoint()
    {
        var result = ExecuteWrite(
            "WAL checkpoint",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    SqlConstants.CheckpointWal;
                command.ExecuteNonQuery();
                return true;
            });

        if (result.Success)
            _logger.LogInformation("SQLite WAL checkpoint 已完成。");

        return result.Success;
    }

    private SqliteConnection CreateAndOpenConnection()
    {
        return CreateAndOpenConnection(
            DatabasePath,
            SqliteOpenMode.ReadWriteCreate,
            pooling: true,
            SqlConstants.ConfigureConnection);
    }

    private SqliteConnection CreateAndOpenConnection(
        string databasePath,
        SqliteOpenMode mode,
        bool pooling,
        string configurationSql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = pooling
        };

        var connection = new SqliteConnection(
            builder.ToString());

        try
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = configurationSql;
            command.ExecuteNonQuery();
            return connection;
        }
        catch (Exception ex)
        {
            connection.Dispose();
            _logger.LogError(
                ex,
                "打开或配置 SQLite 数据库连接失败。数据库={DatabasePath}。",
                databasePath);
            throw;
        }
    }

    private static bool IsBusy(SqliteException exception)
    {
        return exception.SqliteErrorCode is 5 or 6;
    }
}
