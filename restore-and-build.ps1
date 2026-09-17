$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "[1/3] 清理旧的 obj/bin..."
Remove-Item -Recurse -Force .\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\bin -ErrorAction SilentlyContinue

Write-Host "[2/3] 还原 NuGet 包..."
dotnet restore .\ActivityTracker.csproj --configfile .\NuGet.Config
if ($LASTEXITCODE -ne 0) { throw "NuGet 包还原失败。" }

Write-Host "[3/3] 编译项目..."
dotnet build .\ActivityTracker.csproj -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw "项目编译失败。" }

Write-Host ""
Write-Host "完成。现在可以用 Visual Studio 打开 ActivityTracker.csproj 并运行。"
Pause
