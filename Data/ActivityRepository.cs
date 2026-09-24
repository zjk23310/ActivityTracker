using System;
using System.Collections.Generic;
using System.Globalization;

using ActivityTracker.Models;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

public sealed class ActivityRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ActivityRepository(
        SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // 同一个 SessionKey 永远映射到同一行。首次心跳 INSERT，后续心跳和关闭只 UPDATE。
    public DatabaseWriteResult<long> SaveSegment(
        ActivitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(session.SessionKey))
        {
            throw new ArgumentException(
                "会话段必须包含 SessionKey。",
                nameof(session));
        }

        return _connectionFactory.ExecuteWrite(
            "保存活动会话段",
            connection => SaveSegmentCore(
                connection,
                session));
    }

    public List<ActivitySession> GetToday()
    {
        var start = DateTime.Today;
        return GetRange(start, start.AddDays(1));
    }

    public List<ActivitySession> GetRange(
        DateTime start,
        DateTime end,
        bool includeOpen = false)
    {
        if (end <= start)
        {
            throw new ArgumentException(
                "结束时间必须晚于开始时间。");
        }

        var result = new List<ActivitySession>();

        using var connection =
            _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectActivitySessionsByRange;
        command.Parameters.AddWithValue(
            "$startMs", ToUtcMilliseconds(start));
        command.Parameters.AddWithValue(
            "$endMs", ToUtcMilliseconds(end));
        command.Parameters.AddWithValue(
            "$includeOpen",
            includeOpen ? 1 : 0);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(ReadSession(reader));

        return result;
    }

    private static long SaveSegmentCore(
        SqliteConnection connection,
        ActivitySession session)
    {
        using var transaction = connection.BeginTransaction();

        var rowId = session.Id;
        if (rowId <= 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                SqlConstants.InsertOrIgnoreActivitySegment;
            AddAllParameters(insert, session);
            insert.ExecuteNonQuery();

            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText =
                SqlConstants.SelectActivitySegmentId;
            lookup.Parameters.AddWithValue(
                "$sessionKey",
                session.SessionKey);
            rowId = Convert.ToInt64(
                lookup.ExecuteScalar() ?? 0L);

            if (rowId <= 0)
            {
                throw new InvalidOperationException(
                    "插入会话段后无法取得数据库 Id。");
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = session.IsClosed
            ? SqlConstants.UpdateClosedActivitySegment
            : SqlConstants.UpdateOpenActivitySegment;
        AddAllParameters(update, session);
        update.Parameters.AddWithValue("$id", rowId);

        var affectedRows = update.ExecuteNonQuery();
        if (affectedRows == 0 &&
            !session.IsClosed &&
            IsAlreadyClosed(
                connection,
                transaction,
                rowId,
                session.SessionKey))
        {
            // 过期心跳晚于 Close 到达时，数据库中的关闭态具有终局性。
            // 这次写入按幂等成功处理，调用方无需继续重试旧快照。
            transaction.Commit();
            return rowId;
        }

        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                "更新会话段时没有匹配到唯一数据库行。");
        }

        transaction.Commit();
        return rowId;
    }

    private static bool IsAlreadyClosed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rowId,
        string sessionKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            SqlConstants.SelectActivitySegmentClosedState;
        command.Parameters.AddWithValue("$id", rowId);
        command.Parameters.AddWithValue("$sessionKey", sessionKey);

        var value = command.ExecuteScalar();
        return value is not null &&
            value is not DBNull &&
            Convert.ToInt32(value) == 1;
    }

    private static void AddAllParameters(
        SqliteCommand command,
        ActivitySession session)
    {
        command.Parameters.AddWithValue("$process", session.ProcessName);
        command.Parameters.AddWithValue("$title", session.WindowTitle);
        command.Parameters.AddWithValue("$path", session.ExecutablePath);
        command.Parameters.AddWithValue("$appId", session.AppId);
        command.Parameters.AddWithValue("$appName", session.AppName);
        command.Parameters.AddWithValue(
            "$start",
            session.StartTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$end",
            session.EndTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration", session.DurationSeconds);
        command.Parameters.AddWithValue("$idle", session.IsIdle ? 1 : 0);
        command.Parameters.AddWithValue("$sessionKey", session.SessionKey);
        command.Parameters.AddWithValue("$startUtcMs", session.StartUtcMs);
        command.Parameters.AddWithValue("$endUtcMs", session.EndUtcMs);
        command.Parameters.AddWithValue("$localDate", session.LocalDate);
        command.Parameters.AddWithValue("$isClosed", session.IsClosed ? 1 : 0);
        command.Parameters.AddWithValue("$closeReason", session.CloseReason);
        command.Parameters.AddWithValue("$isRecovered", session.IsRecovered ? 1 : 0);
        command.Parameters.AddWithValue("$source", session.Source);
    }

    private static ActivitySession ReadSession(
        SqliteDataReader reader)
    {
        var start = ParseRoundtrip(reader.GetString(6));
        var end = ParseRoundtrip(reader.GetString(7));

        return new ActivitySession
        {
            Id = reader.GetInt64(0),
            ProcessName = reader.GetString(1),
            WindowTitle = reader.GetString(2),
            ExecutablePath = reader.GetString(3),
            AppId = reader.GetString(4),
            AppName = reader.GetString(5),
            StartTime = start.LocalDateTime,
            EndTime = end.LocalDateTime,
            DurationSeconds = reader.GetInt32(8),
            IsIdle = reader.GetInt32(9) != 0,
            SessionKey = reader.GetString(10),
            StartUtcMs = reader.GetInt64(11),
            EndUtcMs = reader.GetInt64(12),
            LocalDate = reader.GetString(13),
            IsClosed = reader.GetInt32(14) != 0,
            CloseReason = reader.GetString(15),
            IsRecovered = reader.GetInt32(16) != 0,
            Source = reader.GetString(17)
        };
    }

    private static DateTimeOffset ParseRoundtrip(string value)
    {
        return DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static long ToUtcMilliseconds(DateTime value)
    {
        var local = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Local)
            : value;
        return new DateTimeOffset(local)
            .ToUniversalTime()
            .ToUnixTimeMilliseconds();
    }
}
