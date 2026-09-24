using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using ActivityTracker.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ActivityTracker.Data;

public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException(
        string message,
        string? backupPath,
        Exception innerException)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    public string? BackupPath { get; }
}

public sealed class DatabaseMigrator
{
    public const int CurrentVersion = 2;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SettingsService _settingsService;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(
        SqliteConnectionFactory connectionFactory,
        SettingsService settingsService,
        ILogger<DatabaseMigrator> logger)
    {
        _connectionFactory = connectionFactory;
        _settingsService = settingsService;
        _logger = logger;
    }

    public void Migrate()
    {
        var oldVersion = 0;
        string? backupPath = null;

        try
        {
            oldVersion = ReadUserVersionFromHeader(
                _connectionFactory.DatabasePath);

            if (File.Exists(_connectionFactory.DatabasePath) &&
                oldVersion < CurrentVersion)
            {
                backupPath = CreateBackup(oldVersion);
            }

            _connectionFactory.Initialize();

            using var connection =
                _connectionFactory.OpenConnection();

            var databaseVersion = ReadUserVersion(connection);

            if (databaseVersion > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"数据库版本 {databaseVersion} 高于程序支持的版本 {CurrentVersion}。");
            }

            if (databaseVersion == 0)
            {
                RunInTransaction(
                    connection,
                    transaction => MigrateV0ToV1(
                        connection,
                        transaction));
                databaseVersion = 1;
            }

            if (databaseVersion == 1)
            {
                RunInTransaction(
                    connection,
                    transaction => MigrateV1ToV2(
                        connection,
                        transaction));
                databaseVersion = 2;
            }

            var (recovered, deleted) =
                RecoverInterruptedSessions(connection);

            _logger.LogInformation(
                "数据库启动清扫完成：收敛未闭合会话 {RecoveredCount} 行，删除空会话 {DeletedCount} 行。",
                recovered,
                deleted);

            var remainingOpenRows = ReadOpenRowCount(connection);
            if (remainingOpenRows > 0)
            {
                _logger.LogError(
                    "数据库启动清扫后仍存在 {OpenRowCount} 条未闭合会话。",
                    remainingOpenRows);
            }

            if (_settingsService.Current.Tracking
                .VerifyIntegrityOnStartup)
            {
                VerifyIntegrity(connection);
            }

            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText =
                    SqlConstants.CheckpointWal;
                checkpoint.ExecuteNonQuery();
            }

            _logger.LogInformation(
                "数据库迁移完成。版本={Version}，备份={BackupPath}。",
                databaseVersion,
                backupPath ?? "无需备份");
        }
        catch (Exception ex) when (ex is not DatabaseMigrationException)
        {
            _logger.LogCritical(
                ex,
                "数据库迁移失败。备份={BackupPath}。",
                backupPath ?? "未生成");

            throw new DatabaseMigrationException(
                "ActivityTracker 无法完成数据库迁移。",
                backupPath,
                ex);
        }
    }

    internal static int ReadUserVersionFromHeader(
        string databasePath)
    {
        if (!File.Exists(databasePath))
            return 0;

        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < 64)
            return 0;

        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length)
            return 0;

        var signature =
            System.Text.Encoding.ASCII.GetString(
                header[..16]);

        if (!string.Equals(
                signature,
                "SQLite format 3\0",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "数据库文件头不是有效的 SQLite 3 格式。");
        }

        return BinaryPrimitives.ReadInt32BigEndian(
            header.Slice(60, 4));
    }

    private string CreateBackup(int oldVersion)
    {
        var directory = Path.GetDirectoryName(
            _connectionFactory.DatabasePath)!;
        var fileName = Path.GetFileName(
            _connectionFactory.DatabasePath);
        var stamp = DateTime.Now.ToString(
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture);
        var baseBackupPath = Path.Combine(
            directory,
            $"{fileName}.bak-v{oldVersion}-{stamp}.db");
        var backupPath = GetAvailableBackupPath(
            baseBackupPath);

        // 这里有意在迁移写入前打开源库，只用于生成一致性快照。
        // 单独 File.Copy 主文件会漏掉尚未 checkpoint 的 WAL 已提交内容。
        CreateConsistentSnapshot(backupPath);
        VerifyBackup(backupPath);

        var backups = Directory
            .EnumerateFiles(
                directory,
                $"{fileName}.bak-v*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ToList();

        foreach (var stale in backups.Skip(2))
            stale.Delete();

        _logger.LogInformation(
            "迁移前数据库备份已创建：{BackupPath}。",
            backupPath);
        return backupPath;
    }

    private void CreateConsistentSnapshot(string backupPath)
    {
        try
        {
            using var source =
                _connectionFactory.OpenReadOnlyConnection();
            using var command = source.CreateCommand();
            command.CommandText = SqlConstants.VacuumInto;
            command.Parameters.AddWithValue(
                "$backupPath",
                backupPath);
            command.ExecuteNonQuery();

            _logger.LogInformation(
                "迁移备份已通过 VACUUM INTO 生成一致性快照。");
        }
        catch (SqliteException ex)
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            _logger.LogWarning(
                ex,
                "VACUUM INTO 不可用，退化为 WAL checkpoint 后复制主数据库文件。");

            if (!_connectionFactory.Checkpoint())
            {
                throw new InvalidOperationException(
                    "无法在迁移前完成 WAL checkpoint，已取消迁移。",
                    ex);
            }

            File.Copy(
                _connectionFactory.DatabasePath,
                backupPath,
                overwrite: false);
        }
    }

    private void VerifyBackup(string backupPath)
    {
        int sourceCount = 0;
        bool sourceHasActivitySessions;

        using (var source =
               _connectionFactory.OpenReadOnlyConnection())
        {
            sourceHasActivitySessions =
                ActivitySessionsTableExists(source);
            if (sourceHasActivitySessions)
                sourceCount = ReadActivitySessionCount(source);
        }

        using var backup =
            _connectionFactory.OpenReadOnlyConnection(backupPath);
        using (var quickCheck = backup.CreateCommand())
        {
            quickCheck.CommandText = SqlConstants.QuickCheck;
            var result = Convert.ToString(
                quickCheck.ExecuteScalar()) ?? "";

            if (!string.Equals(
                    result,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"迁移备份 quick_check 失败：{result}。");
            }
        }

        if (!sourceHasActivitySessions)
            return;

        if (!ActivitySessionsTableExists(backup))
        {
            throw new InvalidDataException(
                "迁移备份缺少 ActivitySessions 表。");
        }

        var backupCount = ReadActivitySessionCount(backup);
        if (backupCount != sourceCount)
        {
            throw new InvalidDataException(
                $"迁移备份活动行数不一致：源库={sourceCount}，备份={backupCount}。");
        }
    }

    private static string GetAvailableBackupPath(
        string baseBackupPath)
    {
        if (!File.Exists(baseBackupPath))
            return baseBackupPath;

        var directory = Path.GetDirectoryName(baseBackupPath)!;
        var fileName = Path.GetFileNameWithoutExtension(
            baseBackupPath);
        var extension = Path.GetExtension(baseBackupPath);

        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(
                directory,
                $"{fileName}-{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static bool ActivitySessionsTableExists(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.ActivitySessionsTableExists;
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static int ReadActivitySessionCount(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectActivitySessionCount;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int ReadOpenRowCount(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectTrackingOpenRowCount;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int ReadUserVersion(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlConstants.GetUserVersion;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void RunInTransaction(
        SqliteConnection connection,
        Action<SqliteTransaction> migration)
    {
        using var transaction = connection.BeginTransaction();
        migration(transaction);
        transaction.Commit();
    }

    private static void MigrateV0ToV1(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            SqlConstants.CreateActivitySessionsTableV1);
        Execute(
            connection,
            transaction,
            SqlConstants.CreateDailyTable);
        Execute(
            connection,
            transaction,
            SqlConstants.CreateTodoItemTable);

        EnsureColumn(connection, transaction,
            "ActivitySessions", "AppId",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "AppName",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, transaction,
            "Daily", "Date",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "Daily", "UpdatedAt",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "Daily", "Title",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "Daily", "Mood", "INTEGER");
        EnsureColumn(connection, transaction,
            "Daily", "Tags",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, transaction,
            "TodoItem", "Note",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "TodoItem", "Priority",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction,
            "TodoItem", "Category",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "TodoItem", "DueTime", "TEXT");
        EnsureColumn(connection, transaction,
            "TodoItem", "SortOrder",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction,
            "TodoItem", "RemindAt", "TEXT");

        BackfillDaily(connection, transaction);

        Execute(
            connection,
            transaction,
            SqlConstants.CreateActivitySessionsV1Indexes);
        Execute(
            connection,
            transaction,
            SqlConstants.SetUserVersion1);
    }

    private static void MigrateV1ToV2(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        EnsureColumn(connection, transaction,
            "ActivitySessions", "SessionKey",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "StartUtcMs",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "EndUtcMs",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "LocalDate",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "IsClosed",
            "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "CloseReason",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "IsRecovered",
            "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction,
            "ActivitySessions", "Source",
            "TEXT NOT NULL DEFAULT 'live'");

        BackfillActivitySessions(connection, transaction);
        Execute(
            connection,
            transaction,
            SqlConstants.CreateActivitySessionsV2Indexes);
        Execute(
            connection,
            transaction,
            SqlConstants.SetUserVersion2);
    }

    private static void BackfillDaily(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            SqlConstants.SelectDailyBackfillRows;

        var rows = new List<(long Id, string CreatedAt, string UpdatedAt, string Date)>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        foreach (var row in rows)
        {
            var created = ParseRoundtrip(row.CreatedAt);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                SqlConstants.UpdateDailyBackfillRow;
            update.Parameters.AddWithValue(
                "$date",
                string.IsNullOrWhiteSpace(row.Date)
                    ? created.LocalDateTime.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)
                    : row.Date);
            update.Parameters.AddWithValue(
                "$updatedAt",
                string.IsNullOrWhiteSpace(row.UpdatedAt)
                    ? row.CreatedAt
                    : row.UpdatedAt);
            update.Parameters.AddWithValue("$id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    private static void BackfillActivitySessions(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        while (true)
        {
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                SqlConstants.SelectActivityBackfillBatch;

            var rows = new List<(long Id, string Start, string End)>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2)));
                }
            }

            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var start = ParseRoundtrip(row.Start);
                var end = ParseRoundtrip(row.End);

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    SqlConstants.UpdateActivityBackfill;
                update.Parameters.AddWithValue(
                    "$startUtcMs",
                    start.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue(
                    "$endUtcMs",
                    end.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue(
                    "$localDate",
                    start.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
            }
        }
    }

    private static (int Recovered, int Deleted)
        RecoverInterruptedSessions(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using var recover = connection.CreateCommand();
        recover.Transaction = transaction;
        recover.CommandText =
            SqlConstants.RecoverOpenActivitySessions;
        var recovered = recover.ExecuteNonQuery();

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            SqlConstants.DeleteEmptyActivitySessions;
        var deleted = delete.ExecuteNonQuery();

        transaction.Commit();
        return (recovered, deleted);
    }

    private void VerifyIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlConstants.QuickCheck;
        var result = Convert.ToString(command.ExecuteScalar()) ?? "";

        if (!string.Equals(
                result,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "SQLite quick_check 返回异常结果：{Result}。",
                result);
        }
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        var exists = false;
        using (var reader = inspect.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(
                        reader.GetString(1),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
            return;

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText =
            $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {definition};";
        alter.ExecuteNonQuery();
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseRoundtrip(string value)
    {
        return DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }
}
