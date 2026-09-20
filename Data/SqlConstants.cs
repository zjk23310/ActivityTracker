namespace ActivityTracker.Data;

// 从各 Repository 中提取的 SQL 语句，避免在业务代码中内联大量 SQL
internal static class SqlConstants
{
    // ==============================
    // ActivitySessions（活动记录）
    // ==============================

    public const string CreateActivitySessionsTable = """
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

    public const string InsertActivitySession = """
        INSERT INTO ActivitySessions
        (ProcessName, WindowTitle, ExecutablePath, StartTime, EndTime, DurationSeconds, IsIdle)
        VALUES
        ($process, $title, $path, $start, $end, $duration, $idle);
        """;

    public const string SelectActivitySessionsByRange = """
        SELECT Id, ProcessName, WindowTitle, ExecutablePath,
               StartTime, EndTime, DurationSeconds, IsIdle
        FROM ActivitySessions
        WHERE StartTime < $end AND StartTime > $start
        ORDER BY StartTime DESC;
        """;

    // ==============================
    // Daily（日记）
    // ==============================

    // 日记归属的日期单独存一列，
    // 不再从 CreatedAt 推导，避免时区换算导致日期偏移
    public const string CreateDailyTable = """
        CREATE TABLE IF NOT EXISTS Daily (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Date TEXT NOT NULL,
            Title TEXT NOT NULL DEFAULT '',
            Content TEXT NOT NULL,
            Mood INTEGER,
            Tags TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """;

    // 用 CreatedAt 回填 Date（按本地时区换算，
    // 不能直接用 date()，它会先把 ISO 串转成 UTC）
    public const string MigrateDailyBackfillDate = """
        UPDATE Daily
        SET Date = date(CreatedAt, 'localtime')
        WHERE Date IS NULL OR Date = '';
        """;

    // 回填 UpdatedAt：旧数据用 CreatedAt 兜底
    public const string MigrateDailyBackfillUpdatedAt = """
        UPDATE Daily
        SET UpdatedAt = CreatedAt
        WHERE UpdatedAt IS NULL OR UpdatedAt = '';
        """;

    public const string InsertDaily = """
        INSERT INTO Daily
        (Date, Title, Content, Mood, Tags, CreatedAt, UpdatedAt)
        VALUES
        ($date, $title, $content, $mood, $tags, $createdAt, $updatedAt);
        """;

    //根据日期获取日记
    public const string SelectDailyByDate = """
        SELECT Id, Date, Title, Content, Mood, Tags, CreatedAt, UpdatedAt
        FROM Daily
        WHERE Date = $date
        ORDER BY CreatedAt DESC;
        """;

    public const string SelectDailyById = """
        SELECT Id, Date, Title, Content, Mood, Tags, CreatedAt, UpdatedAt
        FROM Daily
        WHERE Id = $id;
        """;

    public const string UpdateDaily = """
        UPDATE Daily
        SET Title = $title,
            Content = $content,
            Mood = $mood,
            Tags = $tags,
            UpdatedAt = $updatedAt
        WHERE Id = $id;
        """;

    public const string DeleteDaily = """
        DELETE FROM Daily WHERE Id = $id;
        """;

    // ==============================
    // TodoItem（待办事项）
    // ==============================

    // DueTime 只存 "HH:mm"，为空表示不指定具体时刻
    // RemindAt 预留给以后的提醒功能，暂时没有设置入口
    public const string CreateTodoItemTable = """
        CREATE TABLE IF NOT EXISTS TodoItem (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Note TEXT NOT NULL DEFAULT '',
            IsCompleted INTEGER NOT NULL DEFAULT 0,
            Priority INTEGER NOT NULL DEFAULT 0,
            Category TEXT NOT NULL DEFAULT '',
            DueDate TEXT NOT NULL,
            DueTime TEXT,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            RemindAt TEXT,
            CreatedAt TEXT NOT NULL,
            CompletedAt TEXT
        );
        """;

    // SortOrder 按「同一天」取最大值 +1，保证新加的排在当天最后。
    // 只在当天范围内计数，这样每天的手动顺序都是独立的，
    // 以后做拖拽排序时改这个值也不会影响别的日期。
    public const string InsertTodoItem = """
        INSERT INTO TodoItem
        (Title, Note, IsCompleted, Priority, Category,
         DueDate, DueTime, SortOrder, RemindAt, CreatedAt, CompletedAt)
        VALUES
        ($title, $note, 0, $priority, $category,
         $dueDate, $dueTime,
         (SELECT IFNULL(MAX(SortOrder), 0) + 1
          FROM TodoItem
          WHERE DueDate = $dueDate),
         $remindAt, $createdAt, NULL);
        """;

    // 未完成的排前面，然后按优先级从高到低，再按手动顺序
    public const string SelectTodoItemsByDate = """
        SELECT Id, Title, Note, IsCompleted, Priority, Category,
               DueDate, DueTime, SortOrder, RemindAt, CreatedAt, CompletedAt
        FROM TodoItem
        WHERE DueDate = $dueDate
        ORDER BY IsCompleted ASC, Priority DESC, SortOrder ASC;
        """;

    public const string SelectPendingTodoItems = """
        SELECT Id, Title, Note, IsCompleted, Priority, Category,
               DueDate, DueTime, SortOrder, RemindAt, CreatedAt, CompletedAt
        FROM TodoItem
        WHERE IsCompleted = 0
        ORDER BY Priority DESC, SortOrder ASC;
        """;

    public const string CompleteTodoItem = """
        UPDATE TodoItem
        SET IsCompleted = 1, CompletedAt = $completedAt
        WHERE Id = $id;
        """;

    public const string UncompleteTodoItem = """
        UPDATE TodoItem
        SET IsCompleted = 0, CompletedAt = NULL
        WHERE Id = $id;
        """;

    public const string DeleteTodoItem = """
        DELETE FROM TodoItem WHERE Id = $id;
        """;
}
