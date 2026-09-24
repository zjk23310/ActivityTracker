using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

// 待办事项仓库，管理任务的创建、完成、删除
public sealed class TodoRepository
{
    // 日期 / 时刻统一用这两个格式存储
    private const string DateFormat = "yyyy-MM-dd";

    private readonly SqliteConnectionFactory _connectionFactory;

    public TodoRepository(
        SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // 添加待办事项，SortOrder 由 SQL 内部取最大值 +1
    public void Insert(TodoItem item)
    {
        _connectionFactory.ExecuteWrite(
            "新增待办事项",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.InsertTodoItem;
                command.Parameters.AddWithValue("$title", item.Title);
                command.Parameters.AddWithValue("$note", item.Note);
                command.Parameters.AddWithValue("$priority", item.Priority);
                command.Parameters.AddWithValue("$category", item.Category);
                command.Parameters.AddWithValue(
                    "$dueDate", item.DueDate.Date.ToString(DateFormat));
                command.Parameters.AddWithValue(
                    "$dueTime", (object?)item.DueTime ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$remindAt",
                    item.RemindAt.HasValue
                        ? item.RemindAt.Value.ToString("O")
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "$createdAt", DateTime.Now.ToString("O"));
                return command.ExecuteNonQuery();
            });
    }

    // 获取指定日期的待办事项
    public List<TodoItem> GetByDate(DateTime date)
    {
        var result = new List<TodoItem>();

        using var connection =
            _connectionFactory.OpenConnection();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectTodoItemsByDate;
        command.Parameters.AddWithValue(
            "$dueDate", date.Date.ToString(DateFormat));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadTodoItem(reader));
        }

        return result;
    }

    // 仅获取未完成的待办事项
    public List<TodoItem> GetPending()
    {
        var result = new List<TodoItem>();

        using var connection =
            _connectionFactory.OpenConnection();

        var command = connection.CreateCommand();
        command.CommandText =
            SqlConstants.SelectPendingTodoItems;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadTodoItem(reader));
        }

        return result;
    }

    // 标记为已完成
    public bool Complete(long id)
    {
        var result = _connectionFactory.ExecuteWrite(
            "完成待办事项",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.CompleteTodoItem;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue(
                    "$completedAt", DateTime.Now.ToString("O"));
                return command.ExecuteNonQuery() > 0;
            });
        return result.Success && result.Value;
    }

    // 取消完成标记
    public bool Uncomplete(long id)
    {
        var result = _connectionFactory.ExecuteWrite(
            "取消完成待办事项",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.UncompleteTodoItem;
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery() > 0;
            });
        return result.Success && result.Value;
    }

    // 删除待办事项
    public bool Delete(long id)
    {
        var result = _connectionFactory.ExecuteWrite(
            "删除待办事项",
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = SqlConstants.DeleteTodoItem;
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery() > 0;
            });
        return result.Success && result.Value;
    }

    // 从 reader 中读取一条待办事项
    private static TodoItem ReadTodoItem(
        SqliteDataReader reader)
    {
        return new TodoItem
        {
            Id = reader.GetInt64(0),
            Title = reader.GetString(1),
            Note = reader.GetString(2),
            IsCompleted = reader.GetInt32(3) != 0,
            Priority = reader.GetInt32(4),
            Category = reader.GetString(5),
            DueDate = DateTime.Parse(reader.GetString(6)),
            DueTime = reader.IsDBNull(7)
                ? null
                : reader.GetString(7),
            SortOrder = reader.GetInt32(8),
            RemindAt = reader.IsDBNull(9)
                ? null
                : DateTime.Parse(reader.GetString(9)),
            CreatedAt = DateTime.Parse(reader.GetString(10)),
            CompletedAt = reader.IsDBNull(11)
                ? null
                : DateTime.Parse(reader.GetString(11))
        };
    }
}

// 待办事项条目
public sealed class TodoItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";

    // 备注，补充标题说不完的细节
    public string Note { get; set; } = "";

    public bool IsCompleted { get; set; }

    // 优先级 0=普通 1=重要 2=紧急
    public int Priority { get; set; }

    public string Category { get; set; } = "";

    public DateTime DueDate { get; set; }

    // 截止时刻 "HH:mm"，null 表示不指定具体时刻
    public string? DueTime { get; set; }

    // 手动排序用，以后做拖拽排序时改这个值
    public int SortOrder { get; set; }

    // 提醒时间，预留给以后的提醒功能
    public DateTime? RemindAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // 给界面直接绑定：优先级文字，普通不显示
    public string PriorityText => Priority switch
    {
        2 => "紧急",
        1 => "重要",
        _ => ""
    };

    // 给界面直接绑定：分类 · 截止时刻 · 备注
    public string DetailText
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Category))
                parts.Add(Category);

            if (!string.IsNullOrWhiteSpace(DueTime))
                parts.Add(DueTime);

            if (!string.IsNullOrWhiteSpace(Note))
                parts.Add(Note);

            return string.Join(" · ", parts);
        }
    }
}
