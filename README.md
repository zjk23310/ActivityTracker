# ActivityTracker

Windows / C# / .NET 8 / WPF 的轻量应用使用时间追踪框架。

## 首次运行

1. 安装 Visual Studio 2022，并勾选“使用 .NET 的桌面开发”。
2. 确认安装 .NET 8 SDK。
3. 右键 `restore-and-build.ps1`，选择“使用 PowerShell 运行”；或者在当前目录执行：

```powershell
dotnet restore .\ActivityTracker.csproj --configfile .\NuGet.Config
dotnet build .\ActivityTracker.sln --no-restore
```

4. 用 Visual Studio 打开 `ActivityTracker.csproj`，按 F5。

## 为什么必须 restore

项目使用 NuGet 包 `Microsoft.Data.Sqlite`。`obj\project.assets.json` 是 NuGet 还原时自动生成的文件，不应手工创建或复制。

如果出现以下错误：

- 找不到 `obj\project.assets.json`
- `Microsoft.Data` 不存在
- 找不到 `SqliteConnection`

说明 NuGet 包还原尚未成功。

## 数据库

数据库存放在：

`%LOCALAPPDATA%\ActivityTracker\activity.db`

数据库当前版本为 `user_version=2`。`ActivitySessions` 在保留原始
`ProcessName`、`WindowTitle`、`ExecutablePath`、本地 ISO 时间和应用身份字段的同时，
新增以下可靠性字段：

- `SessionKey`：一次会话段的 GUID；非空值由部分唯一索引约束。
- `StartUtcMs` / `EndUtcMs`：查询、比较和排序使用的 UTC 整数毫秒。
- `LocalDate`：段开始时的本地自然日，格式为 `yyyy-MM-dd`。
- `IsClosed` / `CloseReason`：区分在途行和已结束行，并记录结束原因。
- `IsRecovered` / `Source`：标识崩溃恢复行以及 `live`、`recovered`、`legacy` 来源。

会话段第一次心跳时插入一行，以后心跳和关闭都更新同一行。程序每 2 秒检查一次
定时器断层；超过阈值时只记录睡眠前已经确认的时长，不补造睡眠或空闲数据。
系统挂起、锁屏和会话结束事件会走同一关闭流程。默认在本地午夜把会话切成两行，
因此新数据不会跨越自然日。

关闭快照对同一 `SessionKey` 具有终局性：待重试区只保留最新快照，SQL 也禁止旧的
打开态心跳更新已经关闭的行。挂起后如果恢复广播缺失，但连续 15 次 tick 都在 5 秒
容差内且处于非空闲状态，追踪器会自动解除挂起；8 小时断层或持续空闲不会触发该
看门狗。由于 .NET `PowerModes` 没有 `ResumeAutomatic` 枚举值，后台消息窗口会直接接收
`PBT_APMRESUMEAUTOMATIC` 并复用 Resume 流程。真实 S3、S0 Modern Standby 与远程桌面
恢复仍需要在目标机器上人工验收。

启动时会把上一次崩溃留下的 `IsClosed=0` 行收敛为 `Recovered`，并删除零时长空行。
读取统计时默认排除未闭合行；当前在途段只从内存快照加入一次，避免重复计数。
若手工回拨系统时钟造成原始 UTC 区间重叠，统计读侧会按开始时间排序并裁剪重叠部分，
因此同一毫秒只统计一次；数据库中的原始记录不会被改写。

SQLite 使用 WAL 模式、5 秒 busy timeout、外键检查和 `synchronous=NORMAL`。
连接使用 private cache，减少 WAL 下 shared cache 引起的 `SQLITE_LOCKED` 争用。
程序正常退出时执行 `wal_checkpoint(TRUNCATE)`。`NORMAL` 可以保证进程崩溃后的数据库一致性；若操作系统或硬件突然失效，
最后若干个已提交事务仍可能丢失，这是当前项目为减少常驻写入开销接受的取舍。

从旧版本迁移前会先生成：

`activity.db.bak-v旧版本-yyyyMMdd-HHmmss.db`

同一目录只保留最近 2 份迁移备份。迁移按版本在事务中执行；失败时程序会显示错误并
停止启动追踪，不会带着半迁移数据库继续运行。备份优先使用 `VACUUM INTO` 生成包含
WAL 已提交内容的一致性快照，并立即执行 `quick_check` 与活动行数校验；若运行环境不
支持该语句，才退化为 `wal_checkpoint(TRUNCATE)` 后复制主文件。同一秒发生文件名冲突时
会追加 `-1`、`-2` 等序号。

## 应用身份

`ProcessName`、`WindowTitle` 和 `ExecutablePath` 继续作为原始活动数据保存。
新增的 `AppId` 和 `AppName` 由 `AppIdentityResolver` 统一解析，统计按逻辑
`AppId` 聚合。解析顺序为 Windows Application User Model ID、Package
Family Name、exe 版本资源、规范化路径，最后才是进程名。

旧数据库会通过版本迁移增加 `AppId` 和 `AppName` 两列，不会删除历史活动记录。
历史记录没有 AppId 时，统计服务会根据原始路径和进程名即时解析；旧 exe
已经删除或元数据不足时仍会显示，但可能暂时无法与重装后的应用自动合并。
身份解析集中在一个服务中，后续可以在这里增加用户手动合并／拆分映射。

## 设置

首次运行会生成：

`%LOCALAPPDATA%\ActivityTracker\settings.json`

当前设置包括空闲判定、心跳、断层、超长保护、午夜切分、启动完整性检查，
以及日志最低级别、保留天数和单个日志文件最大体积。
设置写入采用临时文件替换方式；无法解析的设置文件会被备份为
`settings.invalid-日期时间.json`，程序随后恢复默认设置。

默认内容如下：

```json
{
  "schemaVersion": 2,
  "tracking": {
    "idleThresholdMinutes": 5,
    "checkpointSeconds": 30,
    "gapThresholdSeconds": 30,
    "maxSessionMinutes": 360,
    "splitAtMidnight": true,
    "verifyIntegrityOnStartup": false
  },
  "logging": {
    "minimumLevel": "Information",
    "retentionDays": 14,
    "maxFileSizeMb": 10
  }
}
```

`checkpointSeconds` 允许 5–300 秒，`gapThresholdSeconds` 允许 10–300 秒，
`maxSessionMinutes` 允许 30–1440 分钟；越界值会在加载时自动钳制。
追踪器每次 tick 都读取当前设置引用，因此通过 `SettingsService.Save/Reload` 更新的
空闲阈值和上述运行参数会立即生效。当前还没有设置页面，直接编辑 JSON 后仍需让程序
调用 Reload（或重启）才能载入文件变化。

允许的日志级别为 `Trace`、`Debug`、`Information`、`Warning`、
`Error` 和 `Critical`。

## 日志

滚动日志存放在：

`%LOCALAPPDATA%\ActivityTracker\Logs`

日志按日期生成，达到配置的大小上限后生成 `-001`、`-002` 等后续文件，
并自动清理超过保留天数的旧日志。日志不会记录完整窗口标题。
