# ActivityTracker 阶段 3 实施与验证报告

验证日期：2026-09-22（Asia/Shanghai）

## 结果

- 本文记录的是阶段 3 当时的验证结果；其中原先声明的自定义输出目录构建命令无法由
  后续独立验收复现，不能继续作为有效证据。阶段 3.1 已补齐解决方案项目依赖并统一
  `.sln` 行尾，当前构建证据见 `STAGE3.1-VERIFICATION.md`。
- 可靠性测试共 9 项，全部通过。
- 实际旧库已从 `user_version=0` 迁移到 `user_version=2`，1117 条历史活动行完成回填。
- 实际强杀恢复测试的最后心跳距强杀时刻约 12.7 秒，低于 40 秒验收线。
- 当时实际数据库最终 `IsClosed=0` 行数为 0，但 `close_reasons` 中没有 `Shutdown`；
  两条残留行均由下次启动清扫为 `Recovered`。因此这条证据证明了恢复清扫有效，
  不能证明真实进程完成了干净退出。正常退出由合成测试及阶段 3.1 回归测试覆盖。
- 阶段 3 没有修改任何 `.xaml` 文件。

额外安全副本：

`E:\ActivityTracker\Backups\activity-pre-stage3-20260922-205714.db`

迁移器生成的正式备份：

`%LOCALAPPDATA%\ActivityTracker\activity.db.bak-v0-20260922-205729.db`

## 决策落实对照表

| 决策 | 落实位置 | 实现 |
|---|---|---|
| 2.1 一段一行、心跳 UPDATE | `SessionSegmentManager`、`SessionTracker.PersistAction`、`ActivityRepository.SaveSegment` | `SessionKey` 唯一；首次心跳 `INSERT OR IGNORE`，以后按行 Id/SessionKey 更新，关闭幂等 |
| 2.2 UTC 与单调时钟 | `IClock`、`SessionSegmentManager`、`ActivityRepository.GetRange` | 查询使用 UTC 毫秒；持续时长来自 `Stopwatch`；本地 ISO 时间继续保存；旧值用 `ParseExact("O", ..., RoundtripKind)` |
| 2.3 睡眠三保险 | `PowerEventListener`、`SessionTracker`、`ForegroundWindowTracker.Refresh` | 2 秒 tick 断层检测；独立 STA Dispatcher 监听电源/会话事件；恢复后重新检查 Idle 或主动刷新前台窗口 |
| 2.4 午夜切分 | `SessionSegmentManager.ProcessTick` | 旧段结束于 23:59:59.999，新段开始于 00:00:00.000，分别固定自己的 `LocalDate` |
| 2.5 防重复与恢复 | `SqlConstants`、`DatabaseMigrator.RecoverInterruptedSessions`、`ActivityRepository.GetRange` | 部分唯一索引；启动收敛未闭合行；默认读取排除 open 行；当前快照只从内存加入；展示/统计限制超长历史行 |
| 2.6 SQLite 并发 | `SqliteConnectionFactory`、三个 Repository、`TrackingHostedService.StopAsync` | 单一连接工厂；WAL、busy timeout、foreign keys、NORMAL；100/300/900ms 重试；连续失败计数；退出 checkpoint |
| 2.7 迁移框架 | `DatabaseMigrator`、`SqlConstants`、`App.OnStartup` | 文件头读取版本；迁移写入前生成一致性快照；v0→v1→v2 事务迁移；500 行分批 C# 回填；失败弹窗并停止；Repository 不再建表 |
| 2.8 配置 | `AppSettings`、`SettingsService`、`SessionTracker.CreateInput` | schema 2；新增五项配置并钳制；Settings 引用用 `Volatile.Read/Write` 发布；每次 tick 热读取 |

## 自动测试原始输出

执行命令：

```powershell
dotnet E:\ActivityTracker\stage3-validation\final\Debug\net8.0-windows\ActivityTracker.ReliabilityTests.dll
```

输出：

```text
PASS  跨午夜切分
PASS  8 小时心跳断层
PASS  系统时钟回拨
PASS  Suspend/Resume
PASS  双次 Close 幂等
PASS  窗口变化与空闲切换
      migration user_version=2 source=legacy localDate=2026-02-03 startUtcMs=1770084672000 dailyDate=2026-02-03 backups=1
PASS  v0 到 v2 迁移与重复启动幂等
      concurrency rows=1 open=0 writeFailures=0
PASS  会话行幂等与并发读写
      shutdown rows=1 open=0 reason=Shutdown
PASS  SessionTracker 正常停止落库
RESULT  passed=9 failed=0
```

## 实际旧库迁移

迁移后查询结果：

```text
user_version=2
journal_mode=wal
columns=Id,ProcessName,WindowTitle,ExecutablePath,StartTime,EndTime,DurationSeconds,IsIdle,AppId,AppName,SessionKey,StartUtcMs,EndUtcMs,LocalDate,IsClosed,CloseReason,IsRecovered,Source
rows=1118
open_rows=1
legacy_rows=1117
missing_backfill=0
migration_backups=['activity.db.bak-v0-20260922-205729.db']
```

日志证据：

```text
迁移前数据库备份已创建：...activity.db.bak-v0-20260922-205729.db。
数据库启动清扫完成：收敛未闭合会话 0 行，删除空会话 0 行。
数据库迁移完成。版本=2，备份=...activity.db.bak-v0-20260922-205729.db。
```

重复启动迁移器后仍只有 1 个 v0 备份，未重复回填。合成旧库测试还验证了旧 `Daily.CreatedAt`
的 7 位小数 ISO 字符串由 C# 正确回填为 `Date=2026-02-03`。

## 实际强杀与恢复

强杀时刻：

```text
2026-09-22T21:00:16.5420790+08:00
```

强杀后、重启前：

```text
(1118, 'IDLE',
 '2026-09-22T20:57:29.8120347+08:00',
 '2026-09-22T21:00:03.8164771+08:00',
 154, 0, 0, '', '7cd3cbe5-db34-4e52-8a84-ace15bc6479d')
open_rows=1
```

最后心跳距强杀时刻约 12.7 秒。重启后：

```text
(1118, 'IDLE',
 '2026-09-22T20:57:29.8120347+08:00',
 '2026-09-22T21:00:03.8164771+08:00',
 154, 1, 1, 'Recovered', 'recovered',
 '7cd3cbe5-db34-4e52-8a84-ace15bc6479d')
negative_rows=0
over_4h=0
duplicate_keys=0
```

最终清扫状态（来自启动恢复，不是干净退出证据）：

```text
final_open_rows=0
user_version=2
RUNNING_PROCESSES=0
```

## 自测清单状态

| # | 状态 | 证据/限制 |
|---|---|---|
| 1 迁移 | 通过 | 实际 1117 条旧记录迁移；合成旧库验证 Daily；重复迁移不重复备份 |
| 2 崩溃 | 通过 | 实际运行超过 2 分钟后强杀；丢失约 12.7 秒；恢复、重复键和超长检查通过 |
| 3 睡眠 | 部分 | 纯状态机 8 小时断层通过，实际库 `DurationSeconds>4*3600` 为 0；没有让当前工作主机真实休眠 |
| 4 跨天 | 部分 | 假时钟精确验证 23:59:59.999 / 00:00:00.000；没有修改工作主机系统时间等到真实午夜 |
| 5 睡眠定时器 | 部分 | 断层测试验证不补写 8 小时；没有在真实睡眠前临时修改用户配置 |
| 6 锁屏 | 未做破坏性实测 | `PowerEventListener` 映射 `SessionLock→Lock`，状态机 Suspend/Resume 已测；没有主动锁住当前交互会话 |
| 7 时钟跳变 | 部分 | 假时钟回拨测试通过，实际库负时长为 0；没有修改工作主机系统时钟 |
| 8 重复启动 | 通过 | 连续启动 3 次，只有 PID 35972 一个进程；重复 SessionKey 为 0 |
| 9 并发 | 自动化通过 | 60 次同段心跳写入与 100 次交错读取，rows=1、writeFailures=0；实际日志无 SQLITE_BUSY；未人工操作 UI 5 分钟 |
| 10 不变量 | 通过 | 运行中心跳后 open=1；最终退出/清扫后 open=0 |
| 11 单测 | 通过 | 六个必测纯状态机场景全部无真实等待完成；另有迁移、并发、正常停止三项集成测试 |

没有执行真实睡眠、Win+L 和手工改系统时间，因为这些动作会中断当前 Codex 桌面会话，且睡眠后没有
可靠的自动唤醒途径。对应逻辑已用假时钟和实际 SQLite 写入验证，但 S3、S0 Modern Standby、远程桌面
锁定与真实电源广播仍属于需要人工验收的场景。

## 阶段 4 接口建议

本阶段保留了三个可替换接缝：纯 `SessionSegmentManager` 只产生动作；`SessionTracker` 已把外部回调收口到
后台串行队列；数据库写入集中在 `PersistAction`。阶段 4 可以新增不可变的 `ActivityChanged` 消息和
`IActivityEventPublisher`，保留 `SessionChanged` 作为兼容适配器。

建议每个订阅者拥有自己的有界 `Channel<ActivityChanged>`：数据库消费者按顺序完整消费；UI 消费者通过
Dispatcher 合并刷新；Presence 消费者只保留最新状态。发布动作在状态锁外完成，慢订阅者不会阻塞追踪
状态机。数据库落库可进一步抽成 `IActivitySegmentSink`，替换当前 `SessionTracker.PersistAction`，而
`SessionSegmentManager`、Repository 和现有 UI 不需要一起重写。

## 已知风险

- 没有在这台主机上真实验证 S3、休眠、S0 Modern Standby、Win+L、远程桌面断开和多 Windows 会话。
- `SystemEvents` 在无交互桌面或受限会话中可能无法订阅；代码会记录 Warning，并退回心跳断层机制。
- `synchronous=NORMAL` 接受操作系统/硬件突然失效时丢失最后少量已提交事务的风险。
- 手工改系统时钟仍可能让原始 UTC 区间短暂重叠；阶段 3.1 已在统计读侧去重，原始行保持不变。
- 超长历史行只在展示和统计时截断，不会改写用户的历史数据库原始行。
