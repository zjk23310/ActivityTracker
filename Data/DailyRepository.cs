using System;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

// 日记仓库，存储每日文本记录
public sealed class DailyRepository
{
    // 日记日期统一用这个格式存储，便于直接比较
    private const string DateFormat = "yyyy-MM-dd";

    private readonly SqliteConnectionFactory _connectionFactory;

    public DailyRepository(
        SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // 插入一篇日记
    public void Insert(DailyEntry entry)
    {
        var now = DateTime.Now.ToString("O");

        _connectionFactory.ExecuteWrite(
            "新增日记",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.InsertDaily;
                command.Parameters.AddWithValue(
                    "$date", entry.Date.Date.ToString(DateFormat));
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$content", entry.Content);
                command.Parameters.AddWithValue(
                    "$mood", (object?)entry.Mood ?? DBNull.Value);
                command.Parameters.AddWithValue("$tags", entry.Tags);
                command.Parameters.AddWithValue("$createdAt", now);
                command.Parameters.AddWithValue("$updatedAt", now);
                return command.ExecuteNonQuery();
            });
    }

    // 获取指定日期的日记（一天最多一篇）
    public DailyEntry? GetByDate(DateTime date)
    {
        using var connection =
            _connectionFactory.OpenConnection();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectDailyByDate;
        command.Parameters.AddWithValue(
            "$date", date.Date.ToString(DateFormat));

        using var reader = command.ExecuteReader();
        if (reader.Read())
            return ReadEntry(reader);

        return null;
    }

    // 根据 Id 获取单篇日记
    public DailyEntry? GetById(long id)
    {
        using var connection =
            _connectionFactory.OpenConnection();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectDailyById;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (reader.Read())
            return ReadEntry(reader);

        return null;
    }

    // 更新日记内容
    public bool Update(DailyEntry entry)
    {
        var result = _connectionFactory.ExecuteWrite(
            "更新日记",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.UpdateDaily;
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$content", entry.Content);
                command.Parameters.AddWithValue(
                    "$mood", (object?)entry.Mood ?? DBNull.Value);
                command.Parameters.AddWithValue("$tags", entry.Tags);
                command.Parameters.AddWithValue(
                    "$updatedAt", DateTime.Now.ToString("O"));
                command.Parameters.AddWithValue("$id", entry.Id);
                return command.ExecuteNonQuery() > 0;
            });
        return result.Success && result.Value;
    }

    // 删除日记
    public bool Delete(long id)
    {
        var result = _connectionFactory.ExecuteWrite(
            "删除日记",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.DeleteDaily;
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery() > 0;
            });
        return result.Success && result.Value;
    }

    // 从 reader 读取一条日记
    private static DailyEntry ReadEntry(
        SqliteDataReader reader)
    {
        return new DailyEntry
        {
            Id = reader.GetInt64(0),
            Date = DateTime.Parse(reader.GetString(1)),
            Title = reader.GetString(2),
            Content = reader.GetString(3),
            Mood = reader.IsDBNull(4)
                ? null
                : reader.GetInt32(4),
            Tags = reader.GetString(5),
            CreatedAt = DateTime.Parse(reader.GetString(6)),
            UpdatedAt = DateTime.Parse(reader.GetString(7))
        };
    }
}

// 日记条目
public sealed class DailyEntry
{
    public long Id { get; set; }

    // 日记归属的日期（不含时间）
    public DateTime Date { get; set; }

    public string Title { get; set; } = "";

    public string Content { get; set; } = "";

    // 心情 1-5，null 表示未记录
    public int? Mood { get; set; }

    // 标签，多个用逗号分隔
    public string Tags { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    // 最后一次修改时间
    public DateTime UpdatedAt { get; set; }
}
