# ActivityTracker 阶段 3.1 缺陷修复与验证报告

验证日期：2026-09-22（Asia/Shanghai）

## 结论

- 阶段 3.1 的 FIX-1～FIX-5 已完成，P3 的 1～7 项也已完成。
- 永久可靠性测试由 9 项增加到 18 项：`RESULT  passed=18 failed=0`。
- 独立探针从 `PROBE RESULT  failed=4` 变为 `PROBE RESULT  failed=0`。
- 解决方案默认并行构建连续 3 次成功，每次均为 0 warning、0 error。
- 没有修改任何 `.xaml` 文件，没有新增 NuGet 包，没有修改或清空用户实际数据库。
- 实际数据库只使用独立验收提供的只读副本进行核验。

完整原始输出保存在项目目录外，避免把临时验证产物提交到正式项目：

- `E:\ActivityTracker\stage31-validation\reliability-tests.txt`
- `E:\ActivityTracker\stage31-validation\independent-probe-build.txt`
- `E:\ActivityTracker\stage31-validation\independent-probe-after.txt`
- `E:\ActivityTracker\stage31-validation\parallel-builds.txt`

## FIX-1：旧心跳不得重开关闭行

### 根因

`SessionTracker` 原先用普通队列保存失败快照。同一个 `SessionKey` 的旧打开态心跳和新
关闭态快照可以同时存在；旧心跳稍后重放时，原 UPDATE 又允许把 `IsClosed` 从 1 改回
0，并清空关闭原因。

### 改动

- `SessionTracker` 用 `Dictionary<string, PendingWrite>` 与 `LinkedList<string>` 保存待重试项。
  同一 Key 只保留最新快照，同时保留首次入队顺序。
- Close 在访问数据库之前先覆盖同 Key 的排队项；Close 成功后删除对应排队项。
- 已排队的关闭快照拒绝被打开态快照覆盖。
- SQL 拆成带 `AND IsClosed=0` 的心跳 UPDATE 与不带守卫的关闭 UPDATE。
- 心跳 UPDATE 命中 0 行时按 Id + SessionKey 查询：目标已经关闭则按幂等成功处理；目标
  不存在或 Key 不匹配才是真失败。

### 为什么有效

内存队列和 SQLite 各自提供一道终局保护。即使内存因果顺序出现过期重放，数据库也不
允许关闭态回退。按 Id、按 INSERT/唯一 Key 路径以及 200 次交替写入都已覆盖。

## FIX-2：挂起状态必须能自动退出

### 根因

状态机在 `_isSuspended` 时无条件返回。一旦 Windows 没有送达恢复通知，追踪会永久停止。

### 改动

- 挂起期间，tick 单调间隔不超过 5 秒且 `IsIdle=false` 时累计一次；连续 15 次后清除
  挂起，并在当前 tick 内继续正常处理。
- 8 小时大间隔和持续空闲都会重置计数，不会误恢复。
- `Suspend`、`Resume`、`ClearSuspended` 都重置看门狗计数。
- 前台读取失败改为每 5 次才产生一次刷新请求；首次重试记一条 Information。

### PowerModes.ResumeAutomatic 说明

.NET 8 当前的 `Microsoft.Win32.PowerModes` 实际只有 `Resume`、`StatusChange`、
`Suspend`，不存在可编译的 `ResumeAutomatic` 枚举成员。本机枚举反射与 .NET 官方源码
都确认了这一点；官方 `SystemEvents` 实现也没有处理 `PBT_APMRESUMEAUTOMATIC`。
因此 `PowerEventListener` 在原有 STA Dispatcher 线程上增加一个不可见的 WPF 消息窗口，
只接收原始 `WM_POWERBROADCAST/PBT_APMRESUMEAUTOMATIC` 并触发同一个 `Resumed` 事件。
普通恢复继续走 `SystemEvents`。若自动恢复后又收到普通恢复，两次事件由状态机的重复
Resume 幂等行为安全收敛；两类广播都遗漏时再由看门狗兜底。

## FIX-3：迁移备份改为一致性快照

### 根因

直接复制 `activity.db` 不会包含仍在 `activity.db-wal` 中的已提交内容，因此备份可能缺表
或缺行。

### 改动

- 在任何迁移写入前，通过工厂创建的只读连接执行参数化 `VACUUM INTO`。
- 若当前 SQLite 环境不支持该语句，记录 Warning，执行
  `wal_checkpoint(TRUNCATE)` 后再复制主文件。
- 备份生成后立即以只读方式执行 `quick_check`；源库存在 ActivitySessions 时，同时核对
  备份表存在且行数一致。校验失败会中止迁移。
- 同秒文件名冲突追加 `-1`、`-2`，仍只保留最近两份。
- 永久迁移测试删除了预先 checkpoint，并保持一个连接打开，确认 WAL 中的行进入备份。

### 为什么允许先打开连接

这是对阶段 3“打开连接前 File.Copy”的有意修正。该连接只读取源库并生成一致性快照，
不会执行结构或业务数据写入；它解决了 File.Copy 无法看见 WAL 的根本问题。

## FIX-4：取消关停仍等待最后一段

### 根因

`completion.Task.WaitAsync(cancellationToken)` 收到取消后直接抛出，Host 随后可能释放并终止
进程，后台 worker 尚未来得及写入 Shutdown 快照。

### 改动

- 捕获取消，记录 Warning，并继续等待 Shutdown 请求；额外等待有 3 秒具名硬上限。
- 达到硬上限会记 Error；方法之后才释放前台追踪器。
- HostedService 在 tracker 停止后分别于 checkpoint 前后查询 open 行数，连同 checkpoint
  结果记录；任何剩余 open 行都会记 Error。
- `App.OnExit` 仍保持“Stop Host → Dispose Host → Environment.Exit”的顺序。
- `OnForegroundChanged` 增加 `_started` 守卫，迟到事件不能排在 Shutdown 后重开段。

## FIX-5：解决方案构建与旧报告订正

### 发现与处理

独立验收曾稳定观察到默认并行构建无诊断失败。当前会话修改前首次默认构建已经成功，
因此无法再次把该现象确定性复现，不能把根因写成已经证明。可以确认的结构缺陷是：测试
项目虽有 ProjectReference，但 `.sln` 没有显式依赖边，并且文件混用了 CRLF/LF。

本次给测试项目补上 `ProjectSection(ProjectDependencies)`，并把 `.sln` 全部统一为 CRLF。
修改后默认并行构建连续 3 次成功；这证明当前交付状态可复现，但不把历史上的 0/0 失败
武断归因于单一因素。

`STAGE3-VERIFICATION.md` 也已订正：真实库的 open=0 来自下一次启动的 `Recovered`
清扫，不是干净 Shutdown 的证据。干净退出现在由合成回归测试覆盖。

## 其他低成本修复

- SQLite 连接缓存由 Shared 改为 Private。
- 统计采用读侧区间去重：按 UTC 起点排序，后一段有效起点裁剪到已覆盖终点，原始数据库
  行保持不变。
- 启动恢复后自检 open 行数，理论不变量被破坏时记 Error。
- README 已更新一致性备份、挂起看门狗、读侧去重和构建命令。

## 永久测试原始输出

```text
PASS  跨午夜切分
PASS  8 小时心跳断层
PASS  系统时钟回拨
PASS  Suspend/Resume
PASS  双次 Close 幂等
PASS  窗口变化与空闲切换
PASS  挂起看门狗自动恢复
PASS  真实睡眠与空闲不误恢复
PASS  ResumeAutomatic 原始消息映射
PASS  重复 Resume 幂等
      migration user_version=2 source=legacy localDate=2026-02-03 startUtcMs=1770084672000 dailyDate=2026-02-03 backups=1
PASS  v0 到 v2 迁移与重复启动幂等
PASS  关闭行拒绝按 Id 重放旧心跳
PASS  关闭行拒绝按 INSERT 路径重放旧心跳
PASS  同 Key 交替写入最终关闭
      concurrency rows=1 open=0 writeFailures=0
PASS  会话行幂等与并发读写
PASS  统计读取去除时钟回拨重叠
      shutdown rows=1 open=0 reason=Shutdown
PASS  SessionTracker 正常停止落库
PASS  SessionTracker 取消停止仍落库
RESULT  passed=18 failed=0
```

## 独立探针前后对比

| 探针 | 修复前 | 修复后 |
|---|---|---|
| P1 按 Id 重放旧心跳 | FAIL，关闭行被重开 | PASS，关闭态/原因/结束时间不回滚 |
| P2 按 INSERT 路径重放 | FAIL，关闭行被重开 | PASS |
| P3 1300 行迁移 | PASS | PASS |
| P4 缺失 Resume | 缺陷刻画通过，永久挂起 | 期望测试通过，20×2 秒后恢复且只 Open 一次 |
| P5 SessionEnding | PASS | PASS |
| P6 / P6b 午夜与 Gap | PASS | PASS |
| P6c 时钟回拨 | 缺陷刻画通过，原始区间重叠 | 原始记录保留；统计读侧结果无重叠，PASS |
| P7 同 Key 200 次写入 | PASS | PASS |
| P8 WAL 备份 | FAIL，备份缺 ActivitySessions | PASS，备份与主库均为 1 行 |
| P9 真实库只读副本 | PASS | PASS，行数仍为 1124 |
| P10 取消 StopAsync | FAIL，抛 OCE 且立即 open=1 | PASS，不抛且返回时 open=0 |

关键结果原文：

```text
修复前：PROBE RESULT  failed=4
修复后：PROBE RESULT  failed=0
```

探针工程构建 exit=0、0 error；其 2 个 `NU1900` warning 仅表示当前环境无法访问
NuGet 漏洞数据源。正式解决方案的三次 `--no-restore` 构建均为 0 warning、0 error。

## 探针断言变更说明

1. P4 原断言为“120 次 1 分钟 tick 后仍挂起”，它只是在刻画缺陷。按需求改为“20 次
   2 秒非空闲 tick 后自动恢复并只产生一个 Open”。
2. P6c 采用方案 A。原始会话区间仍允许保留重叠，因此没有把状态机断言伪改成原始行
   不重叠；新断言改查 `StatisticsService`，要求读侧总计 90 秒且返回区间不重叠。
3. P10 原探针要求 StopAsync 在独占锁仍由调用线程持有时完成写入，同时又只在 StopAsync
   返回后释放锁。这与有限硬上限逻辑上不可同时满足。新探针让独占锁由后台任务持有
   500ms 后释放，再断言已取消 token 不抛异常且 StopAsync 返回时 open=0。它仍覆盖真实的
   短暂数据库争用与取消关停组合。

临时探针源码只保留在 `%TEMP%`，没有加入正式项目。

## 默认并行构建三次原始结果

```text
BUILD_RUN_1  exit=0  0 个警告  0 个错误  00:00:01.30
BUILD_RUN_2  exit=0  0 个警告  0 个错误  00:00:01.20
BUILD_RUN_3  exit=0  0 个警告  0 个错误  00:00:01.34
```

完整 MSBuild 输出见 `E:\ActivityTracker\stage31-validation\parallel-builds.txt`。

## 完整源文件清单

以下路径指向本次交付后的完整文件，而不是 diff 片段：

- `Services/SessionTracker.cs`
- `Services/SessionSegmentManager.cs`
- `Startup/PowerEventListener.cs`
- `Startup/TrackingHostedService.cs`
- `Data/SqlConstants.cs`
- `Data/ActivityRepository.cs`
- `Data/DatabaseMigrator.cs`
- `Data/SqliteConnectionFactory.cs`
- `Services/StatisticsService.cs`
- `ActivityTracker.ReliabilityTests/Program.cs`
- `ActivityTracker.sln`
- `README.md`
- `STAGE3-VERIFICATION.md`
- `STAGE3.1-VERIFICATION.md`

## “任意时刻最多一个 open 行”是否为硬保证

目前仍是**强化后的软保证**，不是数据库级硬保证。

正常单实例路径中，启动清扫会先关闭历史 open 行，单 worker 状态机只持有一个当前段，
关闭态又不能被旧心跳重开，因此自动化覆盖的正常、取消、睡眠和重试路径均满足最多一个
open 行。但数据库目前只有 SessionKey 唯一索引，没有
`WHERE IsClosed=0` 的全局部分唯一索引；Local Mutex 也允许同一用户在不同 Windows 会话
各运行一个实例。外部写入、跨 Windows 会话双实例或硬上限到点后进程被强制结束，仍可能
在下一次启动清扫前留下异常 open 行。

若要成为硬保证，需要新增数据库版本迁移：先收敛已有重复 open 行，再创建全局部分唯一
索引，并把“关闭旧段 + 打开新段”纳入可被该约束接受的事务顺序。本次按要求没有把版本
从 2 升到 3。

## 已知风险与未执行的人工验收

- 未自动执行真实 S3 睡眠、S0 Modern Standby、Win+L、RDP 断开/重连、修改系统时间和
  真实午夜；这些操作会中断当前开发会话。
- 挂起看门狗只能使用现有 `IsIdle` 信号判断活动。在部分 Modern Standby 机器上仍需确认
  计时器调度和 Windows 输入状态的实际组合。
- `VACUUM INTO` 依赖 SQLite 版本；当前 Microsoft.Data.Sqlite 8 环境已实测通过，旧环境
  会走 checkpoint + copy，并记录 Warning。
- 3 秒是取消后的额外硬上限。磁盘或外部锁超过该上限时方法会记录 Error 后返回，最后一段
  仍可能留待下次启动恢复；HostedService 会把剩余 open 行明确记为 Error。

人工验收建议：

1. 让程序产生一条心跳后进入 S3，唤醒并立即切换两个窗口；确认很快出现新的
   `WindowChanged` 或 `Idle/Resume` 行。
2. 在支持 S0 的机器上执行同样步骤，至少等待 30 秒并确认看门狗没有永久停摆。
3. 分别测试 Win+L、RDP 断开/重连、真实午夜和安全的系统时间回拨。
4. 每次测试后只读执行：

```sql
SELECT Id, ProcessName, StartTime, EndTime, DurationSeconds,
       IsClosed, CloseReason, IsRecovered, Source
FROM ActivitySessions
ORDER BY Id DESC
LIMIT 30;

SELECT COUNT(*) AS open_rows
FROM ActivitySessions
WHERE IsClosed=0;

SELECT SessionKey, COUNT(*) AS duplicate_count
FROM ActivitySessions
WHERE SessionKey<>''
GROUP BY SessionKey
HAVING COUNT(*)>1;

PRAGMA quick_check;
```
