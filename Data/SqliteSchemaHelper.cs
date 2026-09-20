using System;
using Microsoft.Data.Sqlite;

namespace ActivityTracker.Data;

// 建表 / 迁移相关的辅助方法。
// 业务查询 SQL 统一放在 SqlConstants，这里只处理表结构兼容。
internal static class SqliteSchemaHelper
{
    // 表中缺少某一列时就补上，用于兼容旧版本程序建出来的数据库文件
    public static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        if (HasColumn(connection, table, column))
            return;

        var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        command.ExecuteNonQuery();
    }

    // 判断表中是否已存在某一列
    public static bool HasColumn(
        SqliteConnection connection,
        string table,
        string column)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info({table});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // table_info 的第 1 列是列名
            if (string.Equals(
                    reader.GetString(1),
                    column,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
