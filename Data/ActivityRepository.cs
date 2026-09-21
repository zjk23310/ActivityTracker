using System.IO;
using System.Collections.Generic;

using ActivityTracker.Models;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

public sealed class ActivityRepository
{
    private readonly string _connectionString;


    //构造出数据库连接串
    public ActivityRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = $"Data Source={databasePath}";
        Initialize();
    }

   
    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.CreateActivitySessionsTable;
        command.ExecuteNonQuery();
    }

    //����Ự����
    public void Insert(ActivitySession session)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.InsertActivitySession;

        command.Parameters.AddWithValue("$process", session.ProcessName);
        command.Parameters.AddWithValue("$title", session.WindowTitle);
        command.Parameters.AddWithValue("$path", session.ExecutablePath);
        command.Parameters.AddWithValue("$start", session.StartTime.ToString("O"));
        command.Parameters.AddWithValue("$end", session.EndTime.ToString("O"));
        command.Parameters.AddWithValue("$duration", session.DurationSeconds);
        command.Parameters.AddWithValue("$idle", session.IsIdle ? 1 : 0);
        command.ExecuteNonQuery();
    }

    //��ȡ����Ļ��¼
    public List<ActivitySession> GetToday()
    {
        var start = DateTime.Today;
        var end = start.AddDays(1);
        return GetRange(start, end);
    }
    //�����޸ģ���Ϊ��ȡһ��ʱ��εĻ��¼����������������ݷ���
    public List<ActivitySession> GetRange(DateTime start, DateTime end)
    {
        if (end <= start)
            throw new ArgumentException("�����¼�С�ڵ��ڿ�ʼ�¼�");
        var result = new List<ActivitySession>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectActivitySessionsByRange;
        //ע��where�����������ܹ�֧�ֽ���
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
        //result��һ���Ự����
        return result;
    }
}
