# ActivityTracker 阶段 4 验收记录

日期：2026-09-24  
范围：活动变更事件解耦、设计系统基建。未增加页面、统计图表或真实 Presence 对接，未修改 SQLite schema。

## 1. 结果

- 阶段 4 前永久测试基线：`RESULT passed=18 failed=0`。
- 阶段 4 后永久测试：`RESULT passed=28 failed=0`。
- 默认并行构建连续 3 次：每次 `0 warning / 0 error`。
- XAML 硬编码颜色扫描：`RESULT hardcoded-colors=0`。
- 数据库版本保持 `user_version=2`；`Data/SqlConstants.cs` 和迁移版本未因阶段 4 改动。
- 样式画廊在隔离数据库目录实机启动、截图、正常关闭；最终日志为
  `OpenRows=0, PendingWrites=0, CheckpointSucceeded=True`。

> 基线工作区包含阶段 1–3.1 的未提交修改，因此相对仓库 HEAD 的 `git diff` 不能用来
> 区分阶段 3 与阶段 4。阶段 4 没有写入 `MainWindow.xaml.cs` 或
> `Views/StatisticsView.xaml.cs`；新增永久测试逐字断言两处 `SessionChanged` 订阅仍为原形。

## 2. 事件解耦落实对照（2.1–2.8）

| 要求 | 文件 | 类型/方法与结果 |
| --- | --- | --- |
| 2.1 事件模型 | `Services/ActivityChange.cs` | `ActivityChangeKind`、`ActivityTarget`、`ActivityChange`；Open/Heartbeat/Close 映射为 Started/Updated/Ended，Ended 强制非空关闭原因 |
| 2.2 非阻塞总线 | `Services/IActivityChangeBus.cs`、`Services/ActivityChangeBus.cs` | 单一无界入口通道；`Publish` 仅自增序号和 `TryWrite`；每个订阅者独立通道及消费任务；DB 无界、UI/Presence 容量 1 且 DropOldest；`DrainAsync` 返回逐订阅者报告 |
| 2.3 DB sink | `Services/Sinks/ActivityDatabaseSink.cs` | 搬入写库、重试字典和顺序链表；`_rowIds` 缓存；保留关闭态终局规则和引用相等删除；排空后 checkpoint 与未闭合行校验 |
| 2.4 Tracker | `Services/SessionTracker.cs`、`Services/SessionSegmentManager.cs` | Tracker 不再依赖 Repository；只发布事件；`ConfirmPersisted` 改为 `MarkHeartbeatDispatched`；快照行为不变 |
| 2.5 UI | `Startup/IUiDispatcher.cs`、`Startup/WpfUiDispatcher.cs`、`Services/UiActivityNotifier.cs`、`Services/Sinks/ActivityUiSink.cs` | Updated 被忽略；刷新间隔默认 500ms、钳制 100–5000ms；脏位末次补发；UI 事件经 Dispatcher；页面订阅不变 |
| 2.6 Presence 接缝 | `Services/IPresencePublisher.cs`、`Services/Sinks/ActivityPresenceSink.cs` | `IPresenceStateProvider.Current` 最新只读快照；默认日志发布器不输出完整窗口标题；异常在 sink 内隔离 |
| 2.7 DI/关停 | `Startup/ServiceRegistration.cs`、`Startup/TrackingHostedService.cs`、`App.xaml.cs` | 总线 → DB → Presence → UI → Tracking 的相对注册顺序固定；Tracker 先停，DB 最后 drain/checkpoint；App 检查 `BusDrainReport` |
| 2.8 线程契约 | `Services/ActivityChangeBus.cs` XML 注释 | Publish 由 tracker worker 调用；扇出、handler 各在后台任务；UI 经 Dispatcher；前台 hook 线程未改 |

实现使用一条入口通道隔离发布者，再由 dispatcher 向每个订阅者自己的通道扇出：

```text
SessionTracker worker
        │ Publish: Sequence + TryWrite
        ▼
unbounded ingress ── dispatcher
        ├── Database: unbounded, serial handler
        ├── UI: capacity 1, DropOldest
        └── Presence: capacity 1, DropOldest
```

完整窗口标题只存在于事件原始字段和 DB 映射路径。日志与默认 Presence 文案只包含
Kind、AppId、AppName、IsIdle。

## 3. 设计系统落实对照（3.1–3.6）

| 要求 | 文件 | 结果 |
| --- | --- | --- |
| 3.1 目录与聚合 | `Themes/Theme.Dark.xaml`、`App.xaml` | Dark 聚合颜色、排版、尺寸和全部控件字典；画刷均为 DynamicResource，尺寸/字号/圆角均为 StaticResource |
| 3.2 token | `Themes/DesignTokens.Dark.xaml`、`DesignTokens.Light.xaml`、`Typography.xaml`、`Metrics.xaml`、`AppColorPalette.cs` | Dark 值逐项实现；Light 键集合完全相同；色板为自实现稳定 FNV-1a，不使用 `GetHashCode()` |
| 3.3 组件蓝图 | `Themes/Controls.*.xaml` | Card、StatTile、UsageBar、Timeline、ActivityRow、CurrentActivityHero、StatusPresenceCard、EmptyState、FocusRing 均为样式资源，没有进入业务页面布局 |
| 3.4 页面机械替换 | `MainWindow.xaml`、`Views/StatisticsView.xaml`、`DiaryTodoView.xaml`、`AddTodoDialog.xaml` | 仅替换硬编码边距、字号、颜色、圆角并为原有 Border 引用 Card；没有增删业务控件、行列、Tab 或 x:Name |
| 3.5 可核验性 | `Views/Dev/StyleGalleryWindow.xaml(.cs)`、`tools/check-hardcoded-colors.ps1`、永久测试 | `--style-gallery` 仍走单实例；画廊渲染 token、组件和六态；扫描 0 命中；对比度与键集合测试通过 |
| 3.6 校正协议 | 本文第 10 节 | 后续截图校正只修改 token 数值，不修改页面或控件模板 |

画廊截图：`STAGE4-style-gallery.png`（1770×1230，Windows 150% DPI 实机渲染）。

## 4. `ConfirmPersisted` → `MarkHeartbeatDispatched` 的语义变化

阶段 3 的 `ConfirmPersisted` 同时承担两件事：把 SQLite rowId 填回状态机，并推进下一次
心跳的基准。阶段 4 把这两个职责分开：

1. Tracker 把 Heartbeat 转成不可变 `ActivityChange` 并写入无界入口通道。
2. `Publish` 返回后，Tracker 调用 `MarkHeartbeatDispatched(sessionKey, endLocal, monotonic)`，
   只推进心跳节奏。
3. rowId 完全归 `ActivityDatabaseSink._rowIds` 管理；首次保存成功后缓存，后续更新直接使用。

心跳节奏仍然正确，因为基准只在 Heartbeat 已交给不可丢的入口通道后推进；正常关停顺序保证
Tracker 先停、总线后 drain。关闭幂等也不依赖状态机 rowId：sink 仍以 SessionKey 保序，关闭
快照先进入待写队列，旧打开态不能覆盖关闭态；Repository 的 SQL 仍拒绝已关闭行被旧心跳打开。

## 5. 关停顺序证明

`Startup/ServiceRegistration.cs` 中的相对顺序：

```csharp
services.AddSingleton<ActivityChangeBus>();
services.AddSingleton<ActivityDatabaseSink>();
services.AddSingleton<ActivityPresenceSink>();
services.AddSingleton<ActivityUiSink>();
services.AddSingleton<TrackingHostedService>();
```

Generic Host 逆序停止：Tracking 首先发布 Shutdown/Ended；UI 和 Presence 的 Stop 保留订阅；
DB sink 最后调用 `bus.DrainAsync`。Drain 先完成入口通道，dispatcher 再完成各订阅通道，并等待
每个消费循环。因此 DB handler 完成最后一次写入后，DB sink 才处理重试队列、执行
`wal_checkpoint(TRUNCATE)` 和查询未闭合行。

实机隔离运行的原始尾日志：

```text
正在停止单实例唤醒监听。
单实例唤醒监听已停止。
正在停止活动追踪服务。
活动追踪已停止。
活动追踪服务已停止。
SQLite WAL checkpoint 已完成。
数据库消费者停止校验通过：OpenRows=0，PendingWrites=0，CheckpointSucceeded=True。
正在释放 Host。
系统托盘已释放。
```

实机验证还发现停止链曾捕获 UI SynchronizationContext，导致窗口关闭后 Host 卡在
WakeListener。`WakeListenerService`、`TrackingHostedService`、`SessionTracker`、总线和 DB sink
的关停 await 已改用 `ConfigureAwait(false)`；复测进程正常退出。

## 6. 测试与原始输出

### 6.1 默认并行构建三次

命令：

```powershell
1..3 | ForEach-Object {
    dotnet build .\ActivityTracker.sln --no-restore
}
```

原始结果：

```text
BUILD_RUN=1
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:01.81
BUILD_RUN=2
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:00.89
BUILD_RUN=3
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:01.02
```

### 6.2 永久可靠性测试

命令：

```powershell
dotnet .\ActivityTracker.ReliabilityTests\bin\Debug\net8.0-windows\ActivityTracker.ReliabilityTests.dll
```

阶段 4 新增用例的原始输出：

```text
publish totalMs=5.99 p99Ms=0.0047
PASS  事件发布非阻塞
database events=200 rows=1 closed=1 duration=199
PASS  数据库事件零丢失且保序
latest-only calls=2 last=99
PASS  UI 最新值通道丢弃旧值
isolated healthy=1 loggedErrors=1
PASS  订阅者异常隔离并记录
subscription disposed received=1
PASS  事件退订结束消费循环
terminal closed=1 duration=200 staleHeartbeatRejected=1
PASS  DB sink 关闭态终局性
ui notifications=2 intervalMs=500 updatedIgnored=1
PASS  UI 通知节流且补发末次
page subscriptions main=unchanged statistics=unchanged
PASS  页面 SessionChanged 兼容订阅
tokens dark=28 light=28 primary/canvas=15.83
PASS  设计 token 键与对比度
palette distinct=8 stable=1 algorithm=FNV-1a
PASS  应用色板稳定 FNV-1a
RESULT  passed=28 failed=0
```

原有 18 项也全部仍通过；完整输出中的关键关停结果：

```text
shutdown rows=1 open=0 reason=Shutdown
PASS  SessionTracker 正常停止落库
PASS  SessionTracker 取消停止仍落库
```

### 6.3 硬编码颜色扫描

命令：

```powershell
.\tools\check-hardcoded-colors.ps1 Views App.xaml MainWindow.xaml
```

原始输出：

```text
RESULT hardcoded-colors=0
```

### 6.4 样式画廊实机验证

为避免写入用户实际数据库，复制源码到临时目录，并把该副本的 AppPaths 指向临时数据目录，
再运行隔离副本：

```text
STYLE_GALLERY opened=1 exited=1
screenshot=E:\ActivityTracker\ActivityTracker\STAGE4-style-gallery.png
size=1770x1230
```

人工/截图核验表：

| 维度 | 结果 |
| --- | --- |
| Dark Canvas、Surface、Sunken 层级 | 通过 |
| 28 个深浅主题同名颜色 token | 通过；Dark 画廊逐项可见，键集合单测一致 |
| Display/H1/H2/Body/Caption | 通过 |
| Space 1–7 与 Sm/Md/Lg/Pill | 通过 |
| Card/StatTile/UsageBar/Timeline | 通过 |
| ActivityRow/Hero/Presence/Empty/FocusRing | 通过；向下滚动画廊可查看 |
| Normal/Hover/Pressed/Disabled/Focus/Selected | 通过；画廊六态区可交互核验 |
| 关闭与后台清理 | 通过；进程退出、OpenRows=0、checkpoint 成功 |

## 7. 页面兼容证据

永久测试读取源码并逐字检查：

```text
MainWindow.xaml.cs:60:        _sessionTracker.SessionChanged +=
MainWindow.xaml.cs:61:            OnSessionChanged;
Views/StatisticsView.xaml.cs:33:        _sessionTracker.SessionChanged +=
Views/StatisticsView.xaml.cs:34:            OnSessionChanged;
page subscriptions main=unchanged statistics=unchanged
```

阶段 4 仅改 `MainWindow.xaml` 与三个 View XAML 的 token 引用；TabControl、DataGrid 列、页面
控件、行列定义和所有 x:Name 保持不变。

## 8. 改动与新增文件

事件与生命周期：

- `Services/ActivityChange.cs`
- `Services/IActivityChangeBus.cs`
- `Services/ActivityChangeBus.cs`
- `Services/SessionTracker.cs`
- `Services/SessionSegmentManager.cs`
- `Services/UiActivityNotifier.cs`
- `Services/IPresencePublisher.cs`
- `Services/Sinks/ActivityDatabaseSink.cs`
- `Services/Sinks/ActivityUiSink.cs`
- `Services/Sinks/ActivityPresenceSink.cs`
- `Startup/IUiDispatcher.cs`
- `Startup/WpfUiDispatcher.cs`
- `Startup/ServiceRegistration.cs`
- `Startup/TrackingHostedService.cs`
- `Startup/WakeListenerService.cs`
- `Configuration/AppSettings.cs`
- `App.xaml.cs`

设计系统与机械替换：

- `Themes/DesignTokens.Dark.xaml`
- `Themes/DesignTokens.Light.xaml`
- `Themes/Typography.xaml`
- `Themes/Metrics.xaml`
- `Themes/Controls.Button.xaml`
- `Themes/Controls.TextBox.xaml`
- `Themes/Controls.ComboBox.xaml`
- `Themes/Controls.CheckBox.xaml`
- `Themes/Controls.ScrollBar.xaml`
- `Themes/Controls.TabControl.xaml`
- `Themes/Controls.DataGrid.xaml`
- `Themes/Controls.Card.xaml`
- `Themes/Controls.List.xaml`
- `Themes/Controls.Progress.xaml`
- `Themes/Theme.Dark.xaml`
- `Themes/ThemeManager.cs`
- `Themes/AppColorPalette.cs`
- `Views/Dev/StyleGalleryWindow.xaml`
- `Views/Dev/StyleGalleryWindow.xaml.cs`
- `App.xaml`
- `MainWindow.xaml`
- `Views/StatisticsView.xaml`
- `Views/DiaryTodoView.xaml`
- `Views/AddTodoDialog.xaml`
- `tools/check-hardcoded-colors.ps1`
- `STAGE4-style-gallery.png`

验收：

- `ActivityTracker.ReliabilityTests/Program.cs`
- `STAGE4-VERIFICATION.md`

## 9. Publish 非阻塞性结论

对“订阅者是否能阻塞 Publish”而言，这是代码结构上的硬保证：Publish 不遍历订阅者、不调用
handler、不 await、不获取锁，只做 `Interlocked.Increment`、不可变 record 的序号副本和无界入口
Channel 的 `TryWrite`。订阅者再多、单个 handler 再慢，都只会影响 dispatcher/各自队列。

它不是操作系统意义上的硬实时期限。GC 暂停、线程抢占、内存耗尽或进程故障仍可能影响任何托管
代码；`<500ms / P99<1ms` 是回归门槛，不是实时系统证明。要获得严格硬实时保证，需要固定内存、
无 GC/无分配发布路径、实时调度器和进程外故障模型，这不属于 .NET WPF 桌面程序的运行条件。

## 10. 样式 v0 与参考视频的差距、token 校正协议

可以确认：本次实现完整采用需求给定的 Dark 色值、字号、间距、圆角、控件高度、卡片阴影、
有限时长动效和八色应用色板；画廊的层级、卡片、胶囊 Presence、时长数字和进度条均可实机核验。

无法确认：参考视频的逐像素色差、字体实际 fallback、不同 DPI 下的精确像素、阴影扩散、控件
hover/pressed 动效曲线，以及视频作者未公开的布局参数。本阶段没有把这些推断为视频事实。

校正协议：日后拿到清晰截图后，只允许修改 `Themes/DesignTokens.*.xaml` 中的颜色数值，以及
`Typography.xaml` / `Metrics.xaml` 中已存在 token 的数值。`Views/` 与
`Themes/Controls.*.xaml` 不应需要改动；若校正必须修改控件模板或页面，说明 token 抽象不足，
应先补 token，而不是在页面写新的硬编码值。
