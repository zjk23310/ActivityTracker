namespace ActivityTracker.Data;

// 从各 Repository 中提取的 SQL 语句，避免在业务代码中内联大量 SQL
internal static class SqlConstants
{
    public const string EnableWal = "PRAGMA journal_mode=WAL;";
    public const string ConfigureConnection = """
        PRAGMA busy_timeout=5000;
        PRAGMA foreign_keys=ON;
        PRAGMA synchronous=NORMAL;
        """;
    public const string ConfigureReadOnlyConnection = """
        PRAGMA busy_timeout=5000;
        PRAGMA foreign_keys=ON;
        PRAGMA query_only=ON;
        """;
    public const string CheckpointWal =
        "PRAGMA wal_checkpoint(TRUNCATE);";
    public const string VacuumInto =
        "VACUUM INTO $backupPath;";
    public const string GetUserVersion = "PRAGMA user_version;";
    public const string SetUserVersion1 = "PRAGMA user_version=1;";
    public const string SetUserVersion2 = "PRAGMA user_version=2;";
    public const string QuickCheck = "PRAGMA quick_check;";

    // ==============================
    // ActivitySessions（活动记录）
    // ==============================

    public const string CreateActivitySessionsTableV1 = """
        CREATE TABLE IF NOT EXISTS ActivitySessions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProcessName TEXT NOT NULL,
            WindowTitle TEXT NOT NULL,
            ExecutablePath TEXT NOT NULL,
            AppId TEXT NOT NULL DEFAULT '',
            AppName TEXT NOT NULL DEFAULT '',
            StartTime TEXT NOT NULL,
            EndTime TEXT NOT NULL,
            DurationSeconds INTEGER NOT NULL,
            IsIdle INTEGER NOT NULL
        );
        """;

    public const string CreateActivitySessionsV1Indexes = """
        CREATE INDEX IF NOT EXISTS IX_ActivitySessions_StartTime
        ON ActivitySessions(StartTime);
        CREATE INDEX IF NOT EXISTS IX_ActivitySessions_AppId
        ON ActivitySessions(AppId);
        """;

    public const string CreateActivitySessionsV2Indexes = """
        CREATE INDEX IF NOT EXISTS IX_ActivitySessions_StartUtcMs
        ON ActivitySessions(StartUtcMs);
        CREATE INDEX IF NOT EXISTS IX_ActivitySessions_LocalDate
        ON ActivitySessions(LocalDate);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_ActivitySessions_SessionKey
        ON ActivitySessions(SessionKey)
        WHERE SessionKey <> '';
        """;

    public const string InsertOrIgnoreActivitySegment = """
        INSERT OR IGNORE INTO ActivitySessions
        (ProcessName, WindowTitle, ExecutablePath, AppId, AppName,
         StartTime, EndTime, DurationSeconds, IsIdle,
         SessionKey, StartUtcMs, EndUtcMs, LocalDate,
         IsClosed, CloseReason, IsRecovered, Source)
        VALUES
        ($process, $title, $path, $appId, $appName,
         $start, $end, $duration, $idle,
         $sessionKey, $startUtcMs, $endUtcMs, $localDate,
         $isClosed, $closeReason, 0, 'live');
        """;

    public const string SelectActivitySegmentId = """
        SELECT Id
        FROM ActivitySessions
        WHERE SessionKey=$sessionKey;
        """;

    public const string UpdateOpenActivitySegment = """
        UPDATE ActivitySessions
        SET ProcessName=$process,
            WindowTitle=$title,
            ExecutablePath=$path,
            AppId=$appId,
            AppName=$appName,
            StartTime=$start,
            EndTime=$end,
            DurationSeconds=$duration,
            IsIdle=$idle,
            StartUtcMs=$startUtcMs,
            EndUtcMs=$endUtcMs,
            LocalDate=$localDate,
            IsClosed=$isClosed,
            CloseReason=$closeReason,
            IsRecovered=$isRecovered,
            Source=$source
        WHERE Id=$id AND SessionKey=$sessionKey AND IsClosed=0;
        """;

    public const string UpdateClosedActivitySegment = """
        UPDATE ActivitySessions
        SET ProcessName=$process,
            WindowTitle=$title,
            ExecutablePath=$path,
            AppId=$appId,
            AppName=$appName,
            StartTime=$start,
            EndTime=$end,
            DurationSeconds=$duration,
            IsIdle=$idle,
            StartUtcMs=$startUtcMs,
            EndUtcMs=$endUtcMs,
            LocalDate=$localDate,
            IsClosed=$isClosed,
            CloseReason=$closeReason,
            IsRecovered=$isRecovered,
            Source=$source
        WHERE Id=$id AND SessionKey=$sessionKey;
        """;

    public const string SelectActivitySegmentClosedState = """
        SELECT IsClosed
        FROM ActivitySessions
        WHERE Id=$id AND SessionKey=$sessionKey;
        """;

    public const string SelectActivitySessionsByRange = """
        SELECT Id, ProcessName, WindowTitle, ExecutablePath,
               AppId, AppName,
               StartTime, EndTime, DurationSeconds, IsIdle,
               SessionKey, StartUtcMs, EndUtcMs, LocalDate,
               IsClosed, CloseReason, IsRecovered, Source
        FROM ActivitySessions
        WHERE EndUtcMs > $startMs AND StartUtcMs < $endMs
          AND ($includeOpen = 1 OR IsClosed = 1)
        ORDER BY StartUtcMs DESC;
        """;

    public const string SelectActivityBackfillBatch = """
        SELECT Id, StartTime, EndTime
        FROM ActivitySessions
        WHERE StartUtcMs = 0 OR EndUtcMs = 0 OR LocalDate = ''
        ORDER BY Id
        LIMIT 500;
        """;

    public const string UpdateActivityBackfill = """
        UPDATE ActivitySessions
        SET StartUtcMs=$startUtcMs,
            EndUtcMs=$endUtcMs,
            LocalDate=$localDate,
            IsClosed=1,
            IsRecovered=0,
            Source='legacy',
            CloseReason=''
        WHERE Id=$id;
        """;

    public const string RecoverOpenActivitySessions = """
        UPDATE ActivitySessions
        SET IsClosed=1,
            IsRecovered=1,
            CloseReason='Recovered',
            Source='recovered'
        WHERE IsClosed=0;
        """;

    public const string DeleteEmptyActivitySessions = """
        DELETE FROM ActivitySessions
        WHERE DurationSeconds <= 0;
        """;

    public const string SelectTrackingOpenRowCount =
        "SELECT COUNT(*) FROM ActivitySessions WHERE IsClosed = 0;";

    public const string ActivitySessionsTableExists = """
        SELECT EXISTS(
            SELECT 1
            FROM sqlite_master
            WHERE type='table' AND name='ActivitySessions');
        """;

    public const string SelectActivitySessionCount =
        "SELECT COUNT(*) FROM ActivitySessions;";

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

    public const string SelectDailyBackfillRows = """
        SELECT Id, CreatedAt, UpdatedAt, Date
        FROM Daily
        WHERE Date = '' OR UpdatedAt = '';
        """;

    public const string UpdateDailyBackfillRow = """
        UPDATE Daily
        SET Date=$date,
            UpdatedAt=$updatedAt
        WHERE Id=$id;
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
