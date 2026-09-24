using System.Diagnostics;
using System.Collections.Concurrent;
using System.Xml.Linq;

using ActivityTracker.Configuration;
using ActivityTracker.Data;
using ActivityTracker.Logging;
using ActivityTracker.Models;
using ActivityTracker.Services;
using ActivityTracker.Services.Sinks;
using ActivityTracker.Startup;
using ActivityTracker.Themes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
private static readonly TrackingSettings Settings = new()
{
    CheckpointSeconds = 30,
    GapThresholdSeconds = 30,
    MaxSessionMinutes = 360,
    SplitAtMidnight = true,
    IdleThresholdMinutes = 5
};

private static readonly WindowInfo WindowA = new(
    "alpha",
    "Alpha",
    @"C:\Apps\Alpha.exe",
    100);

private static readonly WindowInfo WindowB = new(
    "beta",
    "Beta",
    @"C:\Apps\Beta.exe",
    200);

private static readonly AppIdentity Identity = new(
    "test:app",
    "Test App",
    AppIdentitySource.Persisted);

public static int Main()
{
var tests = new (string Name, Action Run)[]
{
    ("跨午夜切分", TestMidnightSplit),
    ("8 小时心跳断层", TestEightHourGap),
    ("系统时钟回拨", TestClockRollback),
    ("Suspend/Resume", TestSuspendResume),
    ("双次 Close 幂等", TestDoubleClose),
    ("窗口变化与空闲切换", TestWindowAndIdleChanges),
    ("挂起看门狗自动恢复", TestSuspendedWatchdog),
    ("真实睡眠与空闲不误恢复", TestSuspendedWatchdogGuards),
    ("ResumeAutomatic 原始消息映射", TestAutomaticResumeMessage),
    ("重复 Resume 幂等", TestRepeatedResume),
    ("v0 到 v2 迁移与重复启动幂等", TestDatabaseMigration),
    ("关闭行拒绝按 Id 重放旧心跳", TestClosedRowRejectsHeartbeatById),
    ("关闭行拒绝按 INSERT 路径重放旧心跳", TestClosedRowRejectsHeartbeatByInsert),
    ("同 Key 交替写入最终关闭", TestAlternatingOpenClosedWrites),
    ("会话行幂等与并发读写", TestRepositoryConcurrency),
    ("统计读取去除时钟回拨重叠", TestStatisticsOverlapDeduplication),
    ("SessionTracker 正常停止落库", TestSessionTrackerShutdown),
    ("SessionTracker 取消停止仍落库", TestSessionTrackerCancelledShutdown),
    ("事件发布非阻塞", TestActivityBusPublishIsNonBlocking),
    ("数据库事件零丢失且保序", TestActivityBusDatabaseOrdering),
    ("UI 最新值通道丢弃旧值", TestLatestOnlySubscription),
    ("订阅者异常隔离并记录", TestSubscriberExceptionIsolation),
    ("事件退订结束消费循环", TestSubscriptionDispose),
    ("DB sink 关闭态终局性", TestDatabaseSinkTerminalClose),
    ("UI 通知节流且补发末次", TestUiNotifierThrottle),
    ("页面 SessionChanged 兼容订阅", TestPageSessionChangedCompatibility),
    ("设计 token 键与对比度", TestDesignTokenContrast),
    ("应用色板稳定 FNV-1a", TestAppColorPalette),
    ("应用身份 fallback 与同名 exe 隔离", TestAppIdentityFallbacks),
    ("新旧应用身份统计兼容", TestHistoricalIdentityStatistics),
    ("设置持久化与损坏恢复", TestSettingsPersistenceAndRecovery),
    ("文件日志过滤、滚动与保留", TestRollingFileLogger)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT  passed={tests.Length - failures} failed={failures}");
return failures == 0 ? 0 : 1;
}

static void TestMidnightSplit()
{
    var clock = new FakeClock(
        new DateTimeOffset(2026, 1, 1, 23, 59, 58, TimeSpan.FromHours(8)));
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));

    clock.Advance(TimeSpan.FromSeconds(3));
    var actions = manager.ProcessTick(Input(clock, WindowA, false));

    Equal(2, actions.Count, "午夜应产生 Close + Open");
    Equal(SessionCloseReasons.Midnight, actions[0].CloseReason, "关闭原因");
    Equal("2026-01-01", actions[0].LocalDate, "旧段 LocalDate");
    Equal("2026-01-02", actions[1].LocalDate, "新段 LocalDate");
    Equal(23, actions[0].EndLocal.Hour, "旧段结束小时");
    Equal(999, actions[0].EndLocal.Millisecond, "旧段结束毫秒");
    Equal(0, actions[1].StartLocal.Hour, "新段开始小时");
}

static void TestEightHourGap()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));

    clock.Advance(TimeSpan.FromSeconds(2));
    manager.ProcessTick(Input(clock, WindowA, false));

    clock.Advance(TimeSpan.FromHours(8));
    var actions = manager.ProcessTick(Input(clock, WindowA, false));

    Equal(SessionCloseReasons.Gap, actions[0].CloseReason, "断层关闭原因");
    Equal(SessionSegmentActionKind.RefreshForeground, actions[1].Kind, "恢复后主动刷新前台");
    True(actions[0].DurationSeconds < 10, "断层时长不能包含 8 小时睡眠");
    True(manager.LastGapSeconds >= 8 * 3600, "应记录断层秒数");
}

static void TestClockRollback()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));

    clock.Advance(TimeSpan.FromSeconds(2));
    manager.ProcessTick(Input(clock, WindowA, false));
    clock.AdvanceMonotonic(TimeSpan.FromSeconds(2));
    clock.SetWall(clock.LocalNow.AddHours(-1));

    var actions = manager.ProcessTick(Input(clock, WindowA, false));
    Equal(SessionCloseReasons.ClockChange, actions[0].CloseReason, "时钟跳变关闭原因");
    True(actions[0].DurationSeconds >= 0, "时长不能为负");
    True(actions[0].EndUtcMs >= actions[0].StartUtcMs, "UTC 结束不能早于开始");
}

static void TestSuspendResume()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));
    clock.Advance(TimeSpan.FromSeconds(10));

    var close = manager.Suspend(
        SessionCloseReasons.Suspend,
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp);
    Equal(SessionCloseReasons.Suspend, close?.CloseReason, "挂起关闭原因");
    True(manager.IsSuspended, "挂起标志");

    clock.Advance(TimeSpan.FromHours(8));
    var actions = manager.Resume(Input(clock, WindowA, false));
    Equal(SessionSegmentActionKind.Open, actions[0].Kind, "恢复后开新段");
    True(!manager.IsSuspended, "恢复后清除挂起标志");
}

static void TestSuspendedWatchdog()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));
    clock.Advance(TimeSpan.FromSeconds(2));
    manager.Suspend(
        SessionCloseReasons.Suspend,
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp);

    var opened = 0;
    for (var index = 0; index < 20; index++)
    {
        clock.Advance(TimeSpan.FromSeconds(2));
        opened += manager
            .ProcessTick(Input(clock, WindowA, false))
            .Count(action =>
                action.Kind == SessionSegmentActionKind.Open);
    }

    True(!manager.IsSuspended, "持续正常 tick 应触发挂起看门狗");
    Equal(1, opened, "看门狗恢复只能开启一个新段");
}

static void TestSuspendedWatchdogGuards()
{
    var sleepClock = NewClock();
    var sleepManager = new SessionSegmentManager();
    sleepManager.ProcessTick(Input(sleepClock, WindowA, false));
    sleepManager.Suspend(
        SessionCloseReasons.Suspend,
        sleepClock.LocalNow,
        sleepClock.UtcNow.ToUnixTimeMilliseconds(),
        sleepClock.MonotonicTimestamp);
    sleepClock.Advance(TimeSpan.FromHours(8));
    var sleepActions = sleepManager.ProcessTick(
        Input(sleepClock, WindowA, false));
    True(sleepManager.IsSuspended, "8 小时间隔不能误判恢复");
    Equal(0, sleepActions.Count, "真实睡眠形态不能产生动作");

    var idleClock = NewClock();
    var idleManager = new SessionSegmentManager();
    idleManager.ProcessTick(Input(idleClock, WindowA, false));
    idleManager.Suspend(
        SessionCloseReasons.Suspend,
        idleClock.LocalNow,
        idleClock.UtcNow.ToUnixTimeMilliseconds(),
        idleClock.MonotonicTimestamp);

    var idleActionCount = 0;
    for (var index = 0; index < 20; index++)
    {
        idleClock.Advance(TimeSpan.FromSeconds(2));
        idleActionCount += idleManager
            .ProcessTick(Input(idleClock, WindowA, true))
            .Count;
    }

    True(idleManager.IsSuspended, "空闲 tick 不能触发看门狗");
    Equal(0, idleActionCount, "挂起空闲期间不能产生动作");
}

static void TestRepeatedResume()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));
    manager.Suspend(
        SessionCloseReasons.Suspend,
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp);

    clock.Advance(TimeSpan.FromSeconds(2));
    var first = manager.Resume(Input(clock, WindowA, false));
    var second = manager.Resume(Input(clock, WindowA, false));

    Equal(1, first.Count, "首次 Resume 应开启一段");
    Equal(0, second.Count, "重复 Resume 必须无副作用");
}

static void TestAutomaticResumeMessage()
{
    using var listener =
        new ActivityTracker.Startup.PowerEventListener(
            NullLogger<ActivityTracker.Startup.PowerEventListener>.Instance);
    var resumed = 0;
    listener.Resumed += () => resumed++;

    var handler = typeof(ActivityTracker.Startup.PowerEventListener)
        .GetMethod(
            "OnWindowMessage",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
    True(handler is not null, "应存在原始电源消息处理器");

    object?[] arguments =
    {
        IntPtr.Zero,
        0x0218,
        new IntPtr(0x0012),
        IntPtr.Zero,
        false
    };
    var result = handler!.Invoke(listener, arguments);

    Equal(1, resumed, "PBT_APMRESUMEAUTOMATIC 应触发 Resumed");
    Equal(new IntPtr(1), (IntPtr)result!, "自动恢复消息返回值");
    Equal(true, (bool)arguments[4]!, "自动恢复消息应标记为已处理");
}

static void TestDoubleClose()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));
    clock.Advance(TimeSpan.FromSeconds(5));

    var first = manager.Close(
        SessionCloseReasons.Shutdown,
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp);
    var second = manager.Close(
        SessionCloseReasons.Shutdown,
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp);

    True(first is not null, "第一次 Close 应返回动作");
    True(second is null, "第二次 Close 必须 no-op");
}

static void TestWindowAndIdleChanges()
{
    var clock = NewClock();
    var manager = new SessionSegmentManager();
    manager.ProcessTick(Input(clock, WindowA, false));

    clock.Advance(TimeSpan.FromSeconds(2));
    var windowActions = manager.ProcessTick(Input(clock, WindowB, false));
    Equal(SessionCloseReasons.WindowChanged, windowActions[0].CloseReason, "窗口切换原因");
    Equal(SessionSegmentActionKind.Open, windowActions[1].Kind, "窗口切换后开段");

    clock.Advance(TimeSpan.FromSeconds(2));
    var idleActions = manager.ProcessTick(Input(clock, WindowB, true));
    Equal(SessionCloseReasons.Idle, idleActions[0].CloseReason, "进入空闲原因");
    True(idleActions[1].IsIdle, "新段应为空闲段");
}

static void TestDatabaseMigration()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage3-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var databasePath = Path.Combine(directory, "activity.db");
        var factory = new SqliteConnectionFactory(
            databasePath,
            NullLogger<SqliteConnectionFactory>.Instance);
        factory.Initialize();

        var start = new DateTimeOffset(
            2026, 2, 3, 10, 11, 12,
            TimeSpan.FromHours(8));
        var end = start.AddMinutes(5);

        // 保持连接打开，让已提交的旧结构与数据仍可能停留在 WAL 中，
        // 验证迁移备份不是只复制主文件。
        using var keeper = factory.OpenConnection();
        using (var command = keeper.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ActivitySessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProcessName TEXT NOT NULL,
                    WindowTitle TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    DurationSeconds INTEGER NOT NULL,
                    IsIdle INTEGER NOT NULL
                );
                CREATE TABLE Daily (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Content TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                INSERT INTO ActivitySessions
                (ProcessName, WindowTitle, ExecutablePath,
                 StartTime, EndTime, DurationSeconds, IsIdle)
                VALUES
                ('legacy', '不写入日志的标题', 'C:\\Legacy\\legacy.exe',
                 $start, $end, 300, 0);
                INSERT INTO Daily (Content, CreatedAt)
                VALUES ('旧日记', $start);
                PRAGMA user_version=0;
                """;
            command.Parameters.AddWithValue("$start", start.ToString("O"));
            command.Parameters.AddWithValue("$end", end.ToString("O"));
            command.ExecuteNonQuery();
        }

        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        var migrator = new DatabaseMigrator(
            factory,
            settings,
            NullLogger<DatabaseMigrator>.Instance);

        migrator.Migrate();
        var firstBackups = Directory
            .EnumerateFiles(directory, "activity.db.bak-v0-*.db")
            .ToArray();
        var firstBackupCount = firstBackups.Length;

        using (var backup = factory.OpenReadOnlyConnection(
                   firstBackups.Single()))
        {
            using var quickCheck = backup.CreateCommand();
            quickCheck.CommandText = "PRAGMA quick_check;";
            Equal(
                "ok",
                Convert.ToString(quickCheck.ExecuteScalar()),
                "迁移备份 quick_check");

            using var backupCount = backup.CreateCommand();
            backupCount.CommandText =
                "SELECT COUNT(*) FROM ActivitySessions;";
            Equal(
                1,
                Convert.ToInt32(backupCount.ExecuteScalar()),
                "迁移备份必须包含 WAL 中的历史行");
        }

        int userVersion;
        string source;
        string localDate;
        long startUtcMs;
        string dailyDate;

        using (var connection = factory.OpenConnection())
        {
            using var activity = connection.CreateCommand();
            activity.CommandText = """
                SELECT Source, LocalDate, StartUtcMs
                FROM ActivitySessions
                WHERE Id=1;
                """;
            using (var reader = activity.ExecuteReader())
            {
                True(reader.Read(), "迁移后旧活动行应存在");
                source = reader.GetString(0);
                localDate = reader.GetString(1);
                startUtcMs = reader.GetInt64(2);
            }

            using var daily = connection.CreateCommand();
            daily.CommandText = "SELECT Date FROM Daily WHERE Id=1;";
            dailyDate = Convert.ToString(daily.ExecuteScalar()) ?? "";

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(version.ExecuteScalar());
        }

        migrator.Migrate();
        var secondBackupCount = Directory
            .EnumerateFiles(directory, "activity.db.bak-v0-*.db")
            .Count();

        Equal(2, userVersion, "user_version");
        Equal("legacy", source, "历史行 Source");
        Equal("2026-02-03", localDate, "活动 LocalDate");
        Equal(start.ToUnixTimeMilliseconds(), startUtcMs, "活动 StartUtcMs");
        Equal("2026-02-03", dailyDate, "日记 Date C# 回填");
        Equal(1, firstBackupCount, "首次迁移备份数");
        Equal(firstBackupCount, secondBackupCount, "重复迁移不能重复备份");

        Console.WriteLine(
            $"      migration user_version={userVersion} source={source} " +
            $"localDate={localDate} startUtcMs={startUtcMs} " +
            $"dailyDate={dailyDate} backups={secondBackupCount}");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestRepositoryConcurrency()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage3-db-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var databasePath = Path.Combine(directory, "activity.db");
        var factory = new SqliteConnectionFactory(
            databasePath,
            NullLogger<SqliteConnectionFactory>.Instance);
        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        var migrator = new DatabaseMigrator(
            factory,
            settings,
            NullLogger<DatabaseMigrator>.Instance);
        migrator.Migrate();

        var repository = new ActivityRepository(factory);
        var start = new DateTimeOffset(
            2026, 3, 4, 9, 0, 0,
            TimeSpan.FromHours(8));
        var session = NewDatabaseSession(start);
        var first = repository.SaveSegment(session);
        True(first.Success && first.Value > 0, "首次心跳应插入一行");
        session.Id = first.Value;

        var openExcluded = repository.GetRange(
            start.LocalDateTime.AddMinutes(-1),
            start.LocalDateTime.AddMinutes(10));
        var openIncluded = repository.GetRange(
            start.LocalDateTime.AddMinutes(-1),
            start.LocalDateTime.AddMinutes(10),
            includeOpen: true);
        Equal(0, openExcluded.Count, "默认读取应排除在途行");
        Equal(1, openIncluded.Count, "includeOpen 应只返回一行");

        var writer = Task.Run(() =>
        {
            for (var index = 1; index <= 60; index++)
            {
                session.DurationSeconds = index;
                session.EndUtcMs = session.StartUtcMs + index * 1000L;
                session.EndTime = start.AddSeconds(index).LocalDateTime;
                True(
                    repository.SaveSegment(session).Success,
                    "并发心跳写入失败");
            }
        });

        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                var rows = repository.GetRange(
                    start.LocalDateTime.AddMinutes(-1),
                    start.LocalDateTime.AddMinutes(10),
                    includeOpen: true);
                True(rows.Count <= 1, "并发读取出现重复会话行");
            }
        });

        Task.WaitAll(writer, reader);

        session.IsClosed = true;
        session.CloseReason = SessionCloseReasons.Shutdown;
        True(repository.SaveSegment(session).Success, "关闭 UPDATE 失败");

        int rowCount;
        int openCount;
        using (var connection = factory.OpenConnection())
        {
            using var count = connection.CreateCommand();
            count.CommandText = """
                SELECT COUNT(1),
                       SUM(CASE WHEN IsClosed=0 THEN 1 ELSE 0 END)
                FROM ActivitySessions
                WHERE SessionKey=$sessionKey;
                """;
            count.Parameters.AddWithValue("$sessionKey", session.SessionKey);
            using var result = count.ExecuteReader();
            True(result.Read(), "无法读取并发测试结果");
            rowCount = result.GetInt32(0);
            openCount = result.GetInt32(1);
        }

        Equal(1, rowCount, "同一 SessionKey 必须只有一行");
        Equal(0, openCount, "关闭后不能存在未闭合行");
        Equal(0, factory.ConsecutiveWriteFailures, "连续写失败计数");

        Console.WriteLine(
            $"      concurrency rows={rowCount} open={openCount} " +
            $"writeFailures={factory.ConsecutiveWriteFailures}");
        factory.Checkpoint();
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestClosedRowRejectsHeartbeatById()
{
    WithRepository("stale-id", (repository, _, start) =>
    {
        var heartbeat = NewDatabaseSession(start);
        var inserted = repository.SaveSegment(heartbeat);
        True(inserted.Success, "首次心跳写入");
        heartbeat.Id = inserted.Value;

        var closed = NewDatabaseSession(start);
        closed.Id = inserted.Value;
        closed.SessionKey = heartbeat.SessionKey;
        closed.IsClosed = true;
        closed.CloseReason = SessionCloseReasons.Shutdown;
        closed.DurationSeconds = 60;
        closed.EndUtcMs = closed.StartUtcMs + 60_000;
        closed.EndTime = start.AddSeconds(60).LocalDateTime;
        True(repository.SaveSegment(closed).Success, "关闭写入");
        True(repository.SaveSegment(heartbeat).Success, "过期心跳按幂等成功处理");

        var row = ReadOnlySession(repository, start);
        True(row.IsClosed, "旧心跳不能重开关闭行");
        Equal(SessionCloseReasons.Shutdown, row.CloseReason, "关闭原因不能回滚");
        Equal(60, row.DurationSeconds, "结束时长不能回滚");
    });
}

static void TestClosedRowRejectsHeartbeatByInsert()
{
    WithRepository("stale-insert", (repository, _, start) =>
    {
        var key = Guid.NewGuid().ToString("D");
        var heartbeat = NewDatabaseSession(start);
        heartbeat.SessionKey = key;

        var closed = NewDatabaseSession(start);
        closed.SessionKey = key;
        closed.IsClosed = true;
        closed.CloseReason = SessionCloseReasons.Shutdown;
        closed.DurationSeconds = 60;
        closed.EndUtcMs = closed.StartUtcMs + 60_000;
        closed.EndTime = start.AddSeconds(60).LocalDateTime;

        True(repository.SaveSegment(closed).Success, "关闭态首次插入");
        True(repository.SaveSegment(heartbeat).Success, "旧心跳 INSERT 路径幂等成功");

        var row = ReadOnlySession(repository, start);
        True(row.IsClosed, "INSERT 路径旧心跳不能重开关闭行");
        Equal(SessionCloseReasons.Shutdown, row.CloseReason, "关闭原因不能清空");
        Equal(60, row.DurationSeconds, "结束时长不能回滚");
    });
}

static void TestAlternatingOpenClosedWrites()
{
    WithRepository("alternating", (repository, factory, start) =>
    {
        var key = Guid.NewGuid().ToString("D");
        for (var index = 0; index < 200; index++)
        {
            var session = NewDatabaseSession(start);
            session.SessionKey = key;
            session.DurationSeconds = index + 1;
            session.EndUtcMs =
                session.StartUtcMs + (index + 1) * 1000L;
            session.EndTime =
                start.AddSeconds(index + 1).LocalDateTime;
            session.IsClosed = index % 2 == 1;
            session.CloseReason = session.IsClosed
                ? SessionCloseReasons.WindowChanged
                : "";
            True(repository.SaveSegment(session).Success, "交替写入必须成功");
        }

        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN IsClosed=0 THEN 1 ELSE 0 END),
                   MAX(DurationSeconds)
            FROM ActivitySessions
            WHERE SessionKey=$sessionKey;
            """;
        command.Parameters.AddWithValue("$sessionKey", key);
        using var reader = command.ExecuteReader();
        True(reader.Read(), "读取交替写入结果");
        Equal(1, reader.GetInt32(0), "同 Key 只能有一行");
        Equal(0, reader.GetInt32(1), "最终必须为关闭态");
        Equal(200, reader.GetInt32(2), "最终关闭快照必须胜出");
    });
}

static void TestStatisticsOverlapDeduplication()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage31-stats-" +
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = new SqliteConnectionFactory(
            Path.Combine(directory, "activity.db"),
            NullLogger<SqliteConnectionFactory>.Instance);
        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        new DatabaseMigrator(
                factory,
                settings,
                NullLogger<DatabaseMigrator>.Instance)
            .Migrate();
        var repository = new ActivityRepository(factory);
        var start = new DateTimeOffset(
            2026, 3, 4, 9, 0, 0,
            TimeSpan.FromHours(8));

        var first = NewDatabaseSession(start);
        first.IsClosed = true;
        first.CloseReason = SessionCloseReasons.ClockChange;
        first.DurationSeconds = 60;
        first.EndUtcMs = first.StartUtcMs + 60_000;
        first.EndTime = start.AddSeconds(60).LocalDateTime;
        True(repository.SaveSegment(first).Success, "写入第一段");

        var secondStart = start.AddSeconds(30);
        var second = NewDatabaseSession(secondStart);
        second.IsClosed = true;
        second.CloseReason = SessionCloseReasons.Shutdown;
        second.DurationSeconds = 60;
        second.EndUtcMs = second.StartUtcMs + 60_000;
        second.EndTime = secondStart.AddSeconds(60).LocalDateTime;
        True(repository.SaveSegment(second).Success, "写入重叠段");

        var statistics = new StatisticsService(
            repository,
            new AppIdentityResolver(
                NullLogger<AppIdentityResolver>.Instance),
            settings,
            NullLogger<StatisticsService>.Instance);
        var rows = statistics.GetActivitySessions(
            start.LocalDateTime.AddMinutes(-1),
            start.LocalDateTime.AddMinutes(3));

        Equal(2, rows.Count, "重叠裁剪后仍保留两个有效片段");
        Equal(90, rows.Sum(row => row.DurationSeconds), "重叠 30 秒只能统计一次");
        True(
            rows[1].StartUtcMs >= rows[0].EndUtcMs,
            "返回的统计区间不能重叠");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void WithRepository(
    string tag,
    Action<ActivityRepository, SqliteConnectionFactory, DateTimeOffset> test)
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage31-" + tag + "-" +
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = new SqliteConnectionFactory(
            Path.Combine(directory, "activity.db"),
            NullLogger<SqliteConnectionFactory>.Instance);
        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        new DatabaseMigrator(
                factory,
                settings,
                NullLogger<DatabaseMigrator>.Instance)
            .Migrate();

        test(
            new ActivityRepository(factory),
            factory,
            new DateTimeOffset(
                2026, 3, 4, 9, 0, 0,
                TimeSpan.FromHours(8)));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static ActivitySession ReadOnlySession(
    ActivityRepository repository,
    DateTimeOffset start)
{
    var rows = repository.GetRange(
        start.LocalDateTime.AddMinutes(-1),
        start.LocalDateTime.AddMinutes(10),
        includeOpen: true);
    Equal(1, rows.Count, "测试库应只有一个会话段");
    return rows[0];
}

static ActivitySession NewDatabaseSession(DateTimeOffset start)
{
    return new ActivitySession
    {
        ProcessName = "test",
        WindowTitle = "不会进入日志",
        ExecutablePath = @"C:\Apps\test.exe",
        AppId = "test:database",
        AppName = "Database Test",
        StartTime = start.LocalDateTime,
        EndTime = start.AddSeconds(30).LocalDateTime,
        DurationSeconds = 30,
        IsIdle = false,
        SessionKey = Guid.NewGuid().ToString("D"),
        StartUtcMs = start.ToUnixTimeMilliseconds(),
        EndUtcMs = start.AddSeconds(30).ToUnixTimeMilliseconds(),
        LocalDate = start.ToString("yyyy-MM-dd"),
        IsClosed = false,
        Source = "live"
    };
}

static void TestSessionTrackerShutdown()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage3-stop-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = new SqliteConnectionFactory(
            Path.Combine(directory, "activity.db"),
            NullLogger<SqliteConnectionFactory>.Instance);
        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        new DatabaseMigrator(
                factory,
                settings,
                NullLogger<DatabaseMigrator>.Instance)
            .Migrate();

        var repository = new ActivityRepository(factory);
        var bus = new ActivityChangeBus(
            NullLogger<ActivityChangeBus>.Instance);
        var notifier = new UiActivityNotifier(
            new ImmediateUiDispatcher(),
            settings,
            NullLogger<UiActivityNotifier>.Instance);
        var databaseSink = new ActivityDatabaseSink(
            bus,
            repository,
            factory,
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            NullLogger<ActivityDatabaseSink>.Instance);
        databaseSink.StartAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var foreground = new ForegroundWindowTracker();
        var power = new ActivityTracker.Startup.PowerEventListener(
            NullLogger<ActivityTracker.Startup.PowerEventListener>.Instance);
        var manager = new SessionSegmentManager();
        var tracker = new SessionTracker(
            bus,
            notifier,
            new AppIdentityResolver(
                NullLogger<AppIdentityResolver>.Instance),
            foreground,
            settings,
            new SystemClock(),
            manager,
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            power,
            NullLogger<SessionTracker>.Instance);

        tracker.Start();
        Thread.Sleep(TimeSpan.FromSeconds(3));
        tracker.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        databaseSink.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        int rows;
        int openRows;
        string closeReason;
        using (var connection = factory.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(1),
                       SUM(CASE WHEN IsClosed=0 THEN 1 ELSE 0 END),
                       COALESCE(MAX(CloseReason), '')
                FROM ActivitySessions;
                """;
            using var reader = command.ExecuteReader();
            True(reader.Read(), "无法读取正常停止结果");
            rows = reader.GetInt32(0);
            openRows = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            closeReason = reader.GetString(2);
        }

        True(rows >= 1, "正常停止应写入当前段");
        Equal(0, openRows, "正常停止后未闭合行");
        Equal(SessionCloseReasons.Shutdown, closeReason, "正常停止原因");
        Console.WriteLine(
            $"      shutdown rows={rows} open={openRows} reason={closeReason}");

        tracker.Dispose();
        databaseSink.Dispose();
        notifier.Dispose();
        bus.Dispose();
        power.Dispose();
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestSessionTrackerCancelledShutdown()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage31-cancel-stop-" +
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = new SqliteConnectionFactory(
            Path.Combine(directory, "activity.db"),
            NullLogger<SqliteConnectionFactory>.Instance);
        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(directory, "settings.json")));
        new DatabaseMigrator(
                factory,
                settings,
                NullLogger<DatabaseMigrator>.Instance)
            .Migrate();

        var bus = new ActivityChangeBus(
            NullLogger<ActivityChangeBus>.Instance);
        var notifier = new UiActivityNotifier(
            new ImmediateUiDispatcher(),
            settings,
            NullLogger<UiActivityNotifier>.Instance);
        var databaseSink = new ActivityDatabaseSink(
            bus,
            new ActivityRepository(factory),
            factory,
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            NullLogger<ActivityDatabaseSink>.Instance);
        databaseSink.StartAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var power = new ActivityTracker.Startup.PowerEventListener(
            NullLogger<ActivityTracker.Startup.PowerEventListener>.Instance);
        var tracker = new SessionTracker(
            bus,
            notifier,
            new AppIdentityResolver(
                NullLogger<AppIdentityResolver>.Instance),
            new ForegroundWindowTracker(),
            settings,
            new SystemClock(),
            new SessionSegmentManager(),
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            power,
            NullLogger<SessionTracker>.Instance);

        tracker.Start();
        Thread.Sleep(TimeSpan.FromSeconds(3));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watch = Stopwatch.StartNew();
        tracker.StopAsync(cts.Token)
            .GetAwaiter()
            .GetResult();
        databaseSink.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        watch.Stop();

        int openRows;
        string closeReason;
        using (var connection = factory.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SUM(CASE WHEN IsClosed=0 THEN 1 ELSE 0 END),
                       COALESCE(MAX(CloseReason), '')
                FROM ActivitySessions;
                """;
            using var reader = command.ExecuteReader();
            True(reader.Read(), "无法读取取消停止结果");
            openRows = reader.IsDBNull(0)
                ? 0
                : reader.GetInt32(0);
            closeReason = reader.GetString(1);
        }

        Equal(0, openRows, "取消停止后最后一段必须闭合");
        Equal(SessionCloseReasons.Shutdown, closeReason, "取消停止原因");
        True(
            watch.Elapsed < TimeSpan.FromSeconds(3.5),
            "取消停止必须在硬上限内返回");

        tracker.Dispose();
        databaseSink.Dispose();
        notifier.Dispose();
        bus.Dispose();
        power.Dispose();
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestActivityBusPublishIsNonBlocking()
{
    var bus = new ActivityChangeBus(
        NullLogger<ActivityChangeBus>.Instance);
    _ = bus.Subscribe(
        "five-second-handler",
        ActivitySubscriptionOptions.LatestOnly,
        (_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        });

    var samples = new long[10_000];
    var total = Stopwatch.StartNew();
    for (var index = 0; index < samples.Length; index++)
    {
        var start = Stopwatch.GetTimestamp();
        bus.Publish(NewChange(index));
        samples[index] = Stopwatch.GetTimestamp() - start;
    }
    total.Stop();

    Array.Sort(samples);
    var p99 = TimeSpan.FromSeconds(
        samples[(int)(samples.Length * 0.99)] /
        (double)Stopwatch.Frequency);
    True(total.Elapsed < TimeSpan.FromMilliseconds(500),
        $"10000 次 Publish 应小于 500ms，实际 {total.Elapsed.TotalMilliseconds:F2}ms");
    True(p99 < TimeSpan.FromMilliseconds(1),
        $"Publish P99 应小于 1ms，实际 {p99.TotalMilliseconds:F4}ms");
    Console.WriteLine(
        $"      publish totalMs={total.Elapsed.TotalMilliseconds:F2} p99Ms={p99.TotalMilliseconds:F4}");
}

static void TestActivityBusDatabaseOrdering()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage4-bus-db-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = NewMigratedFactory(directory);
        var bus = new ActivityChangeBus(
            NullLogger<ActivityChangeBus>.Instance);
        var sink = new ActivityDatabaseSink(
            bus,
            new ActivityRepository(factory),
            factory,
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            NullLogger<ActivityDatabaseSink>.Instance);
        using var subscription = bus.Subscribe(
            "slow-database",
            ActivitySubscriptionOptions.Database,
            async (change, token) =>
            {
                await Task.Delay(1, token);
                await sink.HandleAsync(change, token);
            });

        const string key = "11111111-1111-1111-1111-111111111111";
        bus.Publish(NewChange(0, ActivityChangeKind.Started, key));
        for (var index = 1; index < 199; index++)
            bus.Publish(NewChange(index, ActivityChangeKind.Updated, key));
        bus.Publish(NewChange(
            199,
            ActivityChangeKind.Ended,
            key,
            SessionCloseReasons.Shutdown));

        var report = bus.DrainAsync(
                TimeSpan.FromSeconds(10),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        True(report.AllDrained, "慢 DB 订阅者必须完整排空");

        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1),
                   MAX(IsClosed),
                   MAX(DurationSeconds),
                   MAX(CloseReason)
            FROM ActivitySessions
            WHERE SessionKey=$key;
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        True(reader.Read(), "无法读取总线 DB 结果");
        Equal(1, reader.GetInt32(0), "同 SessionKey 只能有一行");
        Equal(1, reader.GetInt32(1), "最终行必须关闭");
        Equal(199, reader.GetInt32(2), "最终时长必须来自 Ended");
        Equal(SessionCloseReasons.Shutdown, reader.GetString(3), "最终关闭原因");
        Console.WriteLine("      database events=200 rows=1 closed=1 duration=199");

        sink.Dispose();
        bus.Dispose();
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestLatestOnlySubscription()
{
    var bus = new ActivityChangeBus(
        NullLogger<ActivityChangeBus>.Instance);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var calls = 0;
    var lastDuration = -1;
    using var subscription = bus.Subscribe(
        "latest-ui",
        ActivitySubscriptionOptions.LatestOnly,
        (change, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            Volatile.Write(ref lastDuration, change.DurationSeconds);
            return ValueTask.CompletedTask;
        });

    bus.Publish(NewChange(0));
    True(entered.Wait(TimeSpan.FromSeconds(2)), "UI handler 未开始");
    for (var index = 1; index < 100; index++)
        bus.Publish(NewChange(index));
    release.Set();

    var report = bus.DrainAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    True(report.AllDrained, "UI 最新值订阅必须排空");
    True(calls < 20, $"最新值订阅调用次数应远小于 100，实际 {calls}");
    Equal(99, lastDuration, "最新值订阅必须收到最后一条");
    Console.WriteLine($"      latest-only calls={calls} last={lastDuration}");
    bus.Dispose();
}

static void TestSubscriberExceptionIsolation()
{
    var logger = new CollectingLogger<ActivityChangeBus>();
    var bus = new ActivityChangeBus(logger);
    var received = 0;
    using var throwing = bus.Subscribe(
        "throwing",
        ActivitySubscriptionOptions.Database,
        (_, _) => throw new InvalidOperationException("expected"));
    using var healthy = bus.Subscribe(
        "healthy",
        ActivitySubscriptionOptions.Database,
        (_, _) =>
        {
            Interlocked.Increment(ref received);
            return ValueTask.CompletedTask;
        });

    bus.Publish(NewChange(1));
    var report = bus.DrainAsync(
            TimeSpan.FromSeconds(3),
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    True(report.AllDrained, "异常订阅者也必须结束消费循环");
    Equal(1, received, "健康订阅者不能受异常订阅者影响");
    True(logger.ErrorCount >= 1, "订阅者异常必须记录 Error");
    Console.WriteLine(
        $"      isolated healthy={received} loggedErrors={logger.ErrorCount}");
    bus.Dispose();
}

static void TestSubscriptionDispose()
{
    var bus = new ActivityChangeBus(
        NullLogger<ActivityChangeBus>.Instance);
    var received = 0;
    using var firstReceived = new ManualResetEventSlim();
    var subscription = bus.Subscribe(
        "disposable",
        ActivitySubscriptionOptions.Database,
        (_, _) =>
        {
            Interlocked.Increment(ref received);
            firstReceived.Set();
            return ValueTask.CompletedTask;
        });

    bus.Publish(NewChange(1));
    True(firstReceived.Wait(TimeSpan.FromSeconds(2)), "退订测试首条事件未收到");
    subscription.Dispose();
    bus.Publish(NewChange(2));
    Thread.Sleep(100);
    Equal(1, received, "Dispose 后不得再收到事件");
    bus.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    Console.WriteLine("      subscription disposed received=1");
    bus.Dispose();
}

static void TestDatabaseSinkTerminalClose()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage4-terminal-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var factory = NewMigratedFactory(directory);
        var bus = new ActivityChangeBus(
            NullLogger<ActivityChangeBus>.Instance);
        var repository = new ActivityRepository(factory);
        var sink = new ActivityDatabaseSink(
            bus,
            repository,
            factory,
            new TrackingStatusService(
                factory,
                NullLogger<TrackingStatusService>.Instance),
            NullLogger<ActivityDatabaseSink>.Instance);
        const string key = "22222222-2222-2222-2222-222222222222";

        sink.HandleAsync(
                NewChange(200, ActivityChangeKind.Ended, key, SessionCloseReasons.Shutdown),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        sink.HandleAsync(
                NewChange(100, ActivityChangeKind.Updated, key),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var row = ReadOnlySession(
            repository,
            new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.FromHours(8)));
        True(row.IsClosed, "关闭态不得被旧心跳重新打开");
        Equal(200, row.DurationSeconds, "关闭快照时长不得被旧心跳覆盖");
        Equal(SessionCloseReasons.Shutdown, row.CloseReason, "关闭原因不得丢失");
        Console.WriteLine("      terminal closed=1 duration=200 staleHeartbeatRejected=1");

        sink.Dispose();
        bus.Dispose();
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestUiNotifierThrottle()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-stage4-ui-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var settings = new SettingsService(
            new SettingsRepository(Path.Combine(directory, "settings.json")));
        settings.Current.Tracking.UiRefreshIntervalMs = 500;
        var notifier = new UiActivityNotifier(
            new ImmediateUiDispatcher(),
            settings,
            NullLogger<UiActivityNotifier>.Instance);
        var notifications = 0;
        notifier.SessionChanged += () =>
            Interlocked.Increment(ref notifications);

        notifier.HandleAsync(NewChange(1, ActivityChangeKind.Started), CancellationToken.None);
        notifier.HandleAsync(NewChange(2, ActivityChangeKind.Ended), CancellationToken.None);
        notifier.HandleAsync(NewChange(3, ActivityChangeKind.Updated), CancellationToken.None);
        Equal(1, notifications, "首个可见变化应立即通知");
        Thread.Sleep(650);
        Equal(2, notifications, "节流期间最后一次可见变化必须补发");
        notifier.HandleAsync(NewChange(4, ActivityChangeKind.Updated), CancellationToken.None);
        Thread.Sleep(50);
        Equal(2, notifications, "心跳不得触发 UI 通知");
        Console.WriteLine("      ui notifications=2 intervalMs=500 updatedIgnored=1");
        notifier.Dispose();
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TestDesignTokenContrast()
{
    var themes = Path.Combine(Directory.GetCurrentDirectory(), "Themes");
    True(Directory.Exists(themes), "测试必须从项目根目录运行以读取 Themes");
    var dark = ReadBrushTokens(Path.Combine(themes, "DesignTokens.Dark.xaml"));
    var light = ReadBrushTokens(Path.Combine(themes, "DesignTokens.Light.xaml"));
    True(dark.Keys.ToHashSet().SetEquals(light.Keys), "深浅主题 token 键必须完全一致");

    foreach (var foreground in new[] { "Brush.Text.Primary", "Brush.Text.Secondary" })
    foreach (var background in new[] { "Brush.Bg.Canvas", "Brush.Bg.Surface" })
    {
        var ratio = Contrast(dark[foreground], dark[background]);
        True(ratio >= 4.5, $"{foreground} on {background} 对比度 {ratio:F2} < 4.5");
    }

    True(
        Contrast(dark["Brush.Text.OnAccent"], dark["Brush.Accent"]) >= 4.5,
        "Text.OnAccent on Accent 对比度不足 4.5");
    foreach (var key in new[]
    {
        "Brush.Accent",
        "Brush.State.Success",
        "Brush.State.Warning",
        "Brush.State.Danger",
        "Brush.State.Idle"
    })
    {
        var ratio = Contrast(dark[key], dark["Brush.Bg.Canvas"]);
        True(ratio >= 3.0, $"{key} on Bg.Canvas 对比度 {ratio:F2} < 3.0");
    }

    Console.WriteLine(
        $"      tokens dark={dark.Count} light={light.Count} primary/canvas={Contrast(dark["Brush.Text.Primary"], dark["Brush.Bg.Canvas"]):F2}");
}

static void TestPageSessionChangedCompatibility()
{
    var root = Directory.GetCurrentDirectory();
    const string subscription =
        "_sessionTracker.SessionChanged +=\r\n            OnSessionChanged;";
    var main = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"))
        .Replace("\n", "\r\n")
        .Replace("\r\r\n", "\r\n");
    var statistics = File.ReadAllText(
            Path.Combine(root, "Views", "StatisticsView.xaml.cs"))
        .Replace("\n", "\r\n")
        .Replace("\r\r\n", "\r\n");
    True(main.Contains(subscription, StringComparison.Ordinal),
        "MainWindow SessionChanged 订阅必须保持原形");
    True(statistics.Contains(subscription, StringComparison.Ordinal),
        "StatisticsView SessionChanged 订阅必须保持原形");
    Console.WriteLine("      page subscriptions main=unchanged statistics=unchanged");
}

static void TestAppColorPalette()
{
    var first = AppColorPalette.ForAppId("win32:contoso:editor");
    var again = AppColorPalette.ForAppId("win32:contoso:editor");
    Equal(first, again, "相同 AppId 的颜色必须稳定");
    var colors = Enumerable.Range(0, 128)
        .Select(index => AppColorPalette.ForAppId("app:" + index))
        .Distinct()
        .Count();
    True(colors > 1 && colors <= 8, "色板应稳定映射到 8 个预设颜色");
    Console.WriteLine($"      palette distinct={colors} stable=1 algorithm=FNV-1a");
}

static void TestAppIdentityFallbacks()
{
    var resolver = new AppIdentityResolver(
        NullLogger<AppIdentityResolver>.Instance);
    var root = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-identity-test-" + Guid.NewGuid().ToString("N"));
    var firstPath = Path.Combine(root, "First", "same.exe");
    var otherExePath = Path.Combine(root, "Second", "same.exe");
    var first = resolver.Resolve(
        "same.exe", firstPath, int.MaxValue);
    var samePath = resolver.Resolve(
        "same.exe", firstPath.ToUpperInvariant());
    var otherPath = resolver.Resolve(
        "same.exe", otherExePath);
    var processOnly = resolver.Resolve("same.exe", "");

    Equal(AppIdentitySource.ExecutablePath, first.Source,
        "读取进程失败后应安全回退路径身份");
    Equal(first.AppId, samePath.AppId,
        "相同路径不同大小写应得到相同身份");
    True(first.AppId != otherPath.AppId,
        "不同目录下同名 exe 不应误合并");
    Equal(AppIdentitySource.ProcessName, processOnly.Source,
        "路径缺失时应回退进程名");
    True(!string.IsNullOrWhiteSpace(processOnly.AppId),
        "进程名 fallback 应产生可用 AppId");
}

static void TestHistoricalIdentityStatistics()
{
    WithRepository("identity-stats", (repository, factory, start) =>
    {
        var resolver = new AppIdentityResolver(
            NullLogger<AppIdentityResolver>.Instance);
        var identityRoot = Path.Combine(
            Path.GetDirectoryName(factory.DatabasePath)!,
            "missing-apps");
        var firstPath = Path.Combine(identityRoot, "First", "same.exe");
        var otherPath = Path.Combine(identityRoot, "Second", "same.exe");

        for (var index = 0; index < 4; index++)
        {
            var session = NewDatabaseSession(start.AddSeconds(index * 30));
            session.ProcessName = "same.exe";
            session.ExecutablePath = index == 2
                ? otherPath
                : index == 3 ? "" : firstPath;
            session.AppId = index == 1
                ? resolver.Resolve("same.exe", firstPath).AppId
                : "";
            session.AppName = index == 1 ? "Same App" : "";
            session.IsClosed = true;
            session.CloseReason = SessionCloseReasons.Shutdown;
            True(repository.SaveSegment(session).Success,
                "写入历史身份兼容测试会话");
        }

        var settings = new SettingsService(
            new SettingsRepository(
                Path.Combine(
                    Path.GetDirectoryName(factory.DatabasePath)!,
                    "statistics-settings.json")));
        var statistics = new StatisticsService(
            repository,
            resolver,
            settings,
            NullLogger<StatisticsService>.Instance);
        var usage = statistics.GetAppUsage(
            start.LocalDateTime.AddMinutes(-1),
            start.LocalDateTime.AddMinutes(4));

        Equal(3, usage.Count,
            "旧记录和新记录均应参与统计，且同名异路径应分开");
        Equal(60, usage[0].TotalSeconds,
            "旧路径记录和新 AppId 应合并");
        Equal(120, usage.Sum(row => row.TotalSeconds),
            "历史数据不得因缺少 AppId 消失");
    });
}

static void TestSettingsPersistenceAndRecovery()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-settings-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "settings.json");
        var repository = new SettingsRepository(path);
        var settings = repository.Load();
        True(File.Exists(path), "首次加载应创建设置文件");

        settings.Tracking.IdleThresholdMinutes = 17;
        settings.Tracking.UiRefreshIntervalMs = 50;
        settings.Logging.MinimumLevel = "warning";
        repository.Save(settings);

        var reloaded = new SettingsRepository(path).Load();
        Equal(17, reloaded.Tracking.IdleThresholdMinutes,
            "设置应跨实例持久化");
        Equal(100, reloaded.Tracking.UiRefreshIntervalMs,
            "越界设置应归一化");
        Equal("Warning", reloaded.Logging.MinimumLevel,
            "日志级别应归一化");

        for (var index = 0; index < 2; index++)
        {
            File.WriteAllText(path, "{invalid-json-" + index);
            var recovered = repository.Load();
            Equal(5, recovered.Tracking.IdleThresholdMinutes,
                "损坏设置应恢复默认值");
            True(!string.IsNullOrEmpty(repository.LastRecoveryMessage),
                "损坏恢复应报告备份位置");
        }

        var backups = Directory.GetFiles(
            directory, "settings.invalid-*.json");
        Equal(2, backups.Length,
            "连续损坏恢复不得覆盖先前的备份");
        True(backups.Any(path => File.ReadAllText(path).Contains("invalid-json-0")) &&
             backups.Any(path => File.ReadAllText(path).Contains("invalid-json-1")),
            "两次损坏文件均应保留");
        Console.WriteLine("      settings persisted=1 normalized=1 recovered=2 backups=2");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestRollingFileLogger()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "ActivityTracker-logging-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var expiredPath = Path.Combine(
            directory, "activitytracker-20000101.log");
        File.WriteAllText(expiredPath, "expired");
        File.SetLastWriteTimeUtc(
            expiredPath, DateTime.UtcNow.AddDays(-20));

        using (var provider = new RollingFileLoggerProvider(
                   directory, LogLevel.Information, 14, 1))
        {
            True(!File.Exists(expiredPath),
                "过期日志应在启动时清理");
            var logger = provider.CreateLogger("Regression");
            logger.LogDebug("filtered-marker");
            var payload = new string('x', 900);
            for (var index = 0; index < 1400; index++)
                logger.LogInformation("line {Index} {Payload}", index, payload);
        }

        var files = Directory.GetFiles(
            directory, "activitytracker-*.log");
        True(files.Length >= 2, "达到大小上限时应滚动日志");
        var contents = string.Concat(files.Select(File.ReadAllText));
        True(contents.Contains("line 0 ") &&
             contents.Contains("line 1399 "),
            "滚动后首末日志均应保留");
        True(!contents.Contains("filtered-marker"),
            "低于最低级别的日志不得写入");
        True(files.All(path => new FileInfo(path).Length < 1_050_000),
            "单个日志文件不得显著超过 1 MB 上限");
        Console.WriteLine($"      logs files={files.Length} expiredRemoved=1 filter=1");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static ActivityChange NewChange(
    int duration,
    ActivityChangeKind kind = ActivityChangeKind.Updated,
    string? sessionKey = null,
    string closeReason = "")
{
    var start = new DateTimeOffset(
        2026, 4, 1, 9, 0, 0,
        TimeSpan.FromHours(8));
    var end = start.AddSeconds(duration);
    return new ActivityChange(
        kind,
        sessionKey ?? Guid.NewGuid().ToString("D"),
        0,
        end.ToUniversalTime(),
        start,
        start.ToUnixTimeMilliseconds(),
        end,
        end.ToUnixTimeMilliseconds(),
        duration,
        "2026-04-01",
        new ActivityTarget(
            "test:stage4",
            "Stage 4 Test",
            "stage4",
            @"C:\Apps\stage4.exe",
            "不得写入日志的窗口标题",
            1234,
            false),
        closeReason);
}

static SqliteConnectionFactory NewMigratedFactory(string directory)
{
    var factory = new SqliteConnectionFactory(
        Path.Combine(directory, "activity.db"),
        NullLogger<SqliteConnectionFactory>.Instance);
    var settings = new SettingsService(
        new SettingsRepository(Path.Combine(directory, "settings.json")));
    new DatabaseMigrator(
            factory,
            settings,
            NullLogger<DatabaseMigrator>.Instance)
        .Migrate();
    return factory;
}

static Dictionary<string, string> ReadBrushTokens(string path)
{
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    return XDocument.Load(path)
        .Descendants()
        .Where(element => element.Name.LocalName == "SolidColorBrush")
        .ToDictionary(
            element => element.Attribute(x + "Key")!.Value,
            element => element.Attribute("Color")!.Value,
            StringComparer.Ordinal);
}

static double Contrast(string first, string second)
{
    var lighter = Math.Max(Luminance(first), Luminance(second));
    var darker = Math.Min(Luminance(first), Luminance(second));
    return (lighter + 0.05) / (darker + 0.05);
}

static double Luminance(string color)
{
    var value = color.TrimStart('#');
    if (value.Length == 8)
        value = value[2..];
    var channels = new[]
    {
        Convert.ToInt32(value[0..2], 16) / 255d,
        Convert.ToInt32(value[2..4], 16) / 255d,
        Convert.ToInt32(value[4..6], 16) / 255d
    };
    var linear = channels.Select(channel =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4))
        .ToArray();
    return 0.2126 * linear[0] +
           0.7152 * linear[1] +
           0.0722 * linear[2];
}

static SessionTickInput Input(
    FakeClock clock,
    WindowInfo window,
    bool isIdle)
{
    return new SessionTickInput(
        clock.LocalNow,
        clock.UtcNow.ToUnixTimeMilliseconds(),
        clock.MonotonicTimestamp,
        window,
        isIdle
            ? AppIdentity.Idle
            : Identity,
        isIdle,
        Settings);
}

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class CollectingLogger<T> : ILogger<T>
{
    private int _errorCount;
    public int ErrorCount => Volatile.Read(ref _errorCount);

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error)
            Interlocked.Increment(ref _errorCount);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}

static FakeClock NewClock() => new(
    new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)));

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message}: expected={expected}, actual={actual}");
    }
}

}

internal sealed class FakeClock : IClock
{
    private long _monotonic;

    public FakeClock(DateTimeOffset localNow)
    {
        LocalNow = localNow;
        UtcNow = localNow.ToUniversalTime();
    }

    public DateTimeOffset UtcNow { get; private set; }
    public DateTimeOffset LocalNow { get; private set; }
    public long MonotonicTimestamp => _monotonic;

    public void Advance(TimeSpan duration)
    {
        LocalNow = LocalNow.Add(duration);
        UtcNow = UtcNow.Add(duration);
        AdvanceMonotonic(duration);
    }

    public void AdvanceMonotonic(TimeSpan duration)
    {
        _monotonic += (long)Math.Round(
            duration.TotalSeconds * Stopwatch.Frequency);
    }

    public void SetWall(DateTimeOffset localNow)
    {
        LocalNow = localNow;
        UtcNow = localNow.ToUniversalTime();
    }
}
