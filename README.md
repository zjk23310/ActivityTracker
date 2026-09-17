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
