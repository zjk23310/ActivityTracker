using System.IO;
using System.Collections.Generic;

using ActivityTracker.Models;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

public sealed class ActivityRepository
{
    private readonly string _connectionString;

    
    public ActivityRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = $"Data Source={databasePath}";
        Initialize();
    }

    //初始化，如果没有数据库则创建对应数据库
    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ActivitySessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL,
                ExecutablePath TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                DurationSeconds INTEGER NOT NULL,
                IsIdle INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ActivitySessions_StartTime
            ON ActivitySessions(StartTime);
            """;
        command.ExecuteNonQuery();
    }

    //插入会话操作
    public void Insert(ActivitySession session)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActivitySessions
            (ProcessName, WindowTitle, ExecutablePath, StartTime, EndTime, DurationSeconds, IsIdle)
            VALUES
            ($process, $title, $path, $start, $end, $duration, $idle);
            """;

        command.Parameters.AddWithValue("$process", session.ProcessName);
        command.Parameters.AddWithValue("$title", session.WindowTitle);
        command.Parameters.AddWithValue("$path", session.ExecutablePath);
        command.Parameters.AddWithValue("$start", session.StartTime.ToString("O"));
        command.Parameters.AddWithValue("$end", session.EndTime.ToString("O"));
        command.Parameters.AddWithValue("$duration", session.DurationSeconds);
        command.Parameters.AddWithValue("$idle", session.IsIdle ? 1 : 0);
        command.ExecuteNonQuery();
    }

    //获取今天的活动记录
    public List<ActivitySession> GetToday()
    {
        var start = DateTime.Today;
        var end = start.AddDays(1);
        return GetRange(start, end);
    }
    //进行修改，改为获取一定时间段的活动记录，方便后续进行数据分析
    public List<ActivitySession> GetRange(DateTime start, DateTime end)
    {
        if (end <= start)
            throw new ArgumentException("结束事件小于等于开始事件");
        var result = new List<ActivitySession>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, ExecutablePath,
                   StartTime, EndTime, DurationSeconds, IsIdle
            FROM ActivitySessions
            WHERE StartTime < $end AND StartTime > $start
            ORDER BY StartTime DESC;
            """;
        //注意where参数，这里能够支持交集
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivitySession
            {
                Id = reader.GetInt64(0),
                ProcessName = reader.GetString(1),
                WindowTitle = reader.GetString(2),
                ExecutablePath = reader.GetString(3),
                StartTime = DateTime.Parse(reader.GetString(4)),
                EndTime = DateTime.Parse(reader.GetString(5)),
                DurationSeconds = reader.GetInt32(6),
                IsIdle = reader.GetInt32(7) != 0
            });
        }
        //result是一个会话集合
        return result;
    }
}
