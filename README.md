# ActivityTracker

Windows / C# / .NET 8 / WPF 的轻量应用使用时间追踪框架。

## 首次运行

1. 安装 Visual Studio 2022，并勾选“使用 .NET 的桌面开发”。
2. 确认安装 .NET 8 SDK。
3. 右键 `restore-and-build.ps1`，选择“使用 PowerShell 运行”；或者在当前目录执行：

```powershell
dotnet restore .\ActivityTracker.csproj --configfile .\NuGet.Config
dotnet build .\ActivityTracker.csproj
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

## 应用身份

`ProcessName`、`WindowTitle` 和 `ExecutablePath` 继续作为原始活动数据保存。
新增的 `AppId` 和 `AppName` 由 `AppIdentityResolver` 统一解析，统计按逻辑
`AppId` 聚合。解析顺序为 Windows Application User Model ID、Package
Family Name、exe 版本资源、规范化路径，最后才是进程名。

旧数据库会自动增加 `AppId` 和 `AppName` 两列，不会删除或改写历史记录。
历史记录没有 AppId 时，统计服务会根据原始路径和进程名即时解析；旧 exe
已经删除或元数据不足时仍会显示，但可能暂时无法与重装后的应用自动合并。
身份解析集中在一个服务中，后续可以在这里增加用户手动合并／拆分映射。

## 设置

首次运行会生成：

`%LOCALAPPDATA%\ActivityTracker\settings.json`

当前设置包括空闲判定分钟数、日志最低级别、日志保留天数和单个日志文件最大体积。
设置写入采用临时文件替换方式；无法解析的设置文件会被备份为
`settings.invalid-日期时间.json`，程序随后恢复默认设置。

默认内容如下：

```json
{
  "schemaVersion": 1,
  "tracking": {
    "idleThresholdMinutes": 5
  },
  "logging": {
    "minimumLevel": "Information",
    "retentionDays": 14,
    "maxFileSizeMb": 10
  }
}
```

允许的日志级别为 `Trace`、`Debug`、`Information`、`Warning`、
`Error` 和 `Critical`。当前阶段还没有设置页面，手工修改配置后需重启
ActivityTracker 才会应用新值；设置页面将在后续阶段接入同一个设置服务。

## 日志

滚动日志存放在：

`%LOCALAPPDATA%\ActivityTracker\Logs`

日志按日期生成，达到配置的大小上限后生成 `-001`、`-002` 等后续文件，
并自动清理超过保留天数的旧日志。日志不会记录完整窗口标题。
