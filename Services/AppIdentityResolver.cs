using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using ActivityTracker.Models;

namespace ActivityTracker.Services;

// 将窗口/进程的原始信息解析为逻辑应用身份。
// 这里不读取 WindowTitle，日志中也不会记录 WindowTitle。
// 未来的用户合并/拆分映射应放在 Resolve 开头，优先于自动识别规则。
public sealed class AppIdentityResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const int AppModelErrorNoApplication = 15703;

    private readonly ILogger<AppIdentityResolver> _logger;
    private readonly ConcurrentDictionary<string, byte> _loggedFallbacks =
        new(StringComparer.OrdinalIgnoreCase);

    public AppIdentityResolver(
        ILogger<AppIdentityResolver> logger)
    {
        _logger = logger;//赋值给私有字段_logger，用于记录日志
    }

    public AppIdentity Resolve(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return Resolve(
            window.ProcessName,
            window.ExecutablePath,
            window.ProcessId);
    }

    // processId 只用于进程仍存活时读取 Windows 包身份。
    // 历史记录没有 PID，仍可使用版本资源和 fallback 规则。
    public AppIdentity Resolve(
        string processName,
        string executablePath,
        int? processId = null)
    {
        var safeProcessName = CleanDisplayValue(processName);
        var safePath = executablePath?.Trim() ?? "";

        try
        {
            if (processId is > 0 &&
                TryResolveWindowsIdentity(
                    processId.Value,
                    safeProcessName,
                    out var windowsAppId,
                    out var windowsSource))
            {
                var metadata =
                    TryReadVersionMetadata(
                        safePath,
                        safeProcessName);

                return new AppIdentity(
                    windowsAppId,
                    GetDisplayName(
                        metadata,
                        safePath,
                        safeProcessName),
                    windowsSource);
            }

            var versionMetadata =
                TryReadVersionMetadata(
                    safePath,
                    safeProcessName);

            if (versionMetadata is not null &&
                TryCreateVersionIdentity(
                    versionMetadata,
                    safePath,
                    safeProcessName,
                    out var versionIdentity))
            {
                return versionIdentity;
            }

            if (!string.IsNullOrWhiteSpace(safePath))
            {
                var normalizedPath =
                    NormalizeExecutablePath(safePath);

                if (!string.IsNullOrWhiteSpace(normalizedPath))
                {
                    var identity = new AppIdentity(
                        "path:" + EncodeIdPart(normalizedPath),
                        GetDisplayName(
                            versionMetadata,
                            safePath,
                            safeProcessName),
                        AppIdentitySource.ExecutablePath);

                    LogFallbackOnce(
                        identity.AppId,
                        safeProcessName,
                        "ExecutablePath");

                    return identity;
                }
            }
        }
        catch (Exception ex)
        {
            // 身份解析属于辅助能力，任何失败都不能终止后台追踪。
            _logger.LogWarning(
                ex,
                "应用身份解析发生异常，已回退。Process={ProcessName}",
                safeProcessName);
        }

        var normalizedProcess =
            NormalizeIdentityValue(safeProcessName);

        if (string.IsNullOrWhiteSpace(normalizedProcess))
            normalizedProcess = "unknown";

        var processIdentity = new AppIdentity(
            "process:" + EncodeIdPart(normalizedProcess),
            string.IsNullOrWhiteSpace(safeProcessName)
                ? "未知应用"
                : safeProcessName,
            AppIdentitySource.ProcessName);

        LogFallbackOnce(
            processIdentity.AppId,
            safeProcessName,
            "ProcessName");

        return processIdentity;
    }

    // 尝试解析 Windows 应用身份
    private bool TryResolveWindowsIdentity(
        int processId,
        string processName,
        out string appId,
        out AppIdentitySource source)
    {
        appId = "";
        source = AppIdentitySource.ProcessName;// 默认值，避免未初始化。

        var processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);

        if (processHandle == IntPtr.Zero)
        {
            _logger.LogDebug(
                "无法打开进程以读取 Windows 应用身份。Process={ProcessName}，Win32Error={Win32Error}",
                processName,
                Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (TryGetApplicationUserModelId(
                    processHandle,
                    out var applicationUserModelId,
                    out var applicationResult))
            {
                appId =
                    "aumid:" +
                    EncodeIdPart(
                        NormalizeIdentityValue(
                            applicationUserModelId));
                source = AppIdentitySource.ApplicationUserModelId;
                return true;
            }

            if (applicationResult != AppModelErrorNoApplication &&
                applicationResult != AppModelErrorNoPackage)
            {
                _logger.LogDebug(
                    "读取 Application User Model ID 失败。Process={ProcessName}，Result={Result}",
                    processName,
                    applicationResult);
            }

            if (TryGetPackageFamilyName(
                    processHandle,
                    out var packageFamilyName,
                    out var packageResult))
            {
                appId =
                    "package:" +
                    EncodeIdPart(
                        NormalizeIdentityValue(
                            packageFamilyName));
                source = AppIdentitySource.PackageFamilyName;
                return true;
            }

            if (packageResult != AppModelErrorNoPackage)
            {
                _logger.LogDebug(
                    "读取 Package Family Name 失败。Process={ProcessName}，Result={Result}",
                    processName,
                    packageResult);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }

        return false;
    }

    private VersionMetadata? TryReadVersionMetadata(
        string executablePath,
        string processName)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        try
        {
            if (!File.Exists(executablePath))
                return null;

            var info =
                FileVersionInfo.GetVersionInfo(executablePath);

            return new VersionMetadata(
                CleanDisplayValue(info.CompanyName),
                CleanDisplayValue(info.ProductName),
                CleanDisplayValue(info.OriginalFilename),
                CleanDisplayValue(info.FileDescription));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "读取 exe 版本资源失败。Process={ProcessName}",
                processName);
            return null;
        }
    }

    private static bool TryCreateVersionIdentity(
        VersionMetadata metadata,
        string executablePath,
        string processName,
        out AppIdentity identity)
    {
        var company =
            NormalizeIdentityValue(metadata.CompanyName);
        var product =
            NormalizeIdentityValue(metadata.ProductName);
        var originalFilename =
            NormalizeOriginalFilename(metadata.OriginalFilename);
        var description =
            NormalizeIdentityValue(metadata.FileDescription);

        string? appId = null;

        if (company.Length > 0 && product.Length > 0)
        {
            // Windows 系统组件常共享同一个宽泛 ProductName。
            // 这种情况下加入 OriginalFilename，避免把多个系统程序合并。
            if (IsGenericProductName(product) &&
                originalFilename.Length > 0)
            {
                appId = BuildId(
                    "win32-file",
                    company,
                    product,
                    originalFilename);
            }
            else
            {
                // 产品级身份可以跨安装目录、版本目录，并可合并同产品多个 exe。
                appId = BuildId(
                    "win32-product",
                    company,
                    product);
            }
        }
        else if (product.Length > 0 &&
                 originalFilename.Length > 0)
        {
            appId = BuildId(
                "win32-product-file",
                product,
                originalFilename);
        }
        else if (company.Length > 0 &&
                 originalFilename.Length > 0)
        {
            appId = BuildId(
                "win32-company-file",
                company,
                originalFilename);
        }
        else if (originalFilename.Length > 0 &&
                 description.Length > 0)
        {
            appId = BuildId(
                "win32-file-description",
                originalFilename,
                description);
        }

        if (appId is null)
        {
            identity = null!;
            return false;
        }

        identity = new AppIdentity(
            appId,
            GetDisplayName(
                metadata,
                executablePath,
                processName),
            AppIdentitySource.VersionResource);
        return true;
    }

    private static string GetDisplayName(
        VersionMetadata? metadata,
        string executablePath,
        string processName)
    {
        if (metadata is not null)
        {
            var product =
                CleanDisplayValue(metadata.ProductName);

            if (product.Length > 0 &&
                !IsGenericProductName(
                    NormalizeIdentityValue(product)))
            {
                return product;
            }

            var description =
                CleanDisplayValue(metadata.FileDescription);

            if (description.Length > 0)
                return description;

            if (product.Length > 0)
                return product;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var fileName =
                    Path.GetFileNameWithoutExtension(executablePath);

                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            catch
            {
                // 路径可能来自已经退出或受保护的进程，继续使用进程名。
            }
        }

        return string.IsNullOrWhiteSpace(processName)
            ? "未知应用"
            : processName;
    }

    private static string NormalizeExecutablePath(string path)
    {
        var trimmed = path.Trim().Trim('"');

        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // 无效或已经不可访问的路径仍可作为原始 fallback 使用。
        }

        return trimmed
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
    }

    // 清理显示用的字符串：去掉多余空格、全角半角统一、去掉首尾空格。
    private static string CleanDisplayValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(
            " ",
            value.Normalize(NormalizationForm.FormKC)
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeIdentityValue(string? value)
    {
        return CleanDisplayValue(value).ToLowerInvariant();
    }

    private static string NormalizeOriginalFilename(string? value)
    {
        var normalized = NormalizeIdentityValue(value);

        // Windows 的本地化版本资源有时返回 foo.exe.mui，
        // 同一文件复制到其他目录后则返回 foo.exe。
        if (normalized.EndsWith(
                ".mui",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private static bool IsGenericProductName(string normalizedProductName)
    {
        return normalizedProductName.Contains(
                   "operating system",
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedProductName.Equals(
                   "windows",
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedProductName.Equals(
                   "microsoft windows",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildId(
        string prefix,
        params string[] parts)
    {
        return prefix + ":" +
               string.Join("|", parts.Select(EncodeIdPart));
    }

    // 编码 ID 部分
    private static string EncodeIdPart(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private void LogFallbackOnce(
        string appId,
        string processName,
        string fallback)
    {
        if (!_loggedFallbacks.TryAdd(appId, 0))
            return;

        _logger.LogDebug(
            "应用身份使用 {Fallback} fallback。Process={ProcessName}",
            fallback,
            processName);
    }

    // 尝试获取应用的用户模型 ID
    private static bool TryGetApplicationUserModelId(
        IntPtr processHandle,
        out string value,
        out int result)
    {
        uint length = 0;
        result = GetApplicationUserModelId(
            processHandle,
            ref length,
            null);

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            value = "";
            return false;
        }

        var buffer = new StringBuilder((int)length);
        result = GetApplicationUserModelId(
            processHandle,
            ref length,
            buffer);

        value = result == ErrorSuccess
            ? buffer.ToString()
            : "";

        return result == ErrorSuccess && value.Length > 0;
    }

    private static bool TryGetPackageFamilyName(
        IntPtr processHandle,
        out string value,
        out int result)
    {
        uint length = 0;
        result = GetPackageFamilyName(
            processHandle,
            ref length,
            null);

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            value = "";
            return false;
        }

        var buffer = new StringBuilder((int)length);
        result = GetPackageFamilyName(
            processHandle,
            ref length,
            buffer);

        value = result == ErrorSuccess
            ? buffer.ToString()
            : "";

        return result == ErrorSuccess && value.Length > 0;
    }

    private sealed record VersionMetadata(
        string CompanyName,
        string ProductName,
        string OriginalFilename,
        string FileDescription);

    //用于读取 Windows 应用身份的 Win32 API
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        IntPtr processHandle,
        ref uint applicationUserModelIdLength,
        [Out] StringBuilder? applicationUserModelId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(
        IntPtr processHandle,
        ref uint packageFamilyNameLength,
        [Out] StringBuilder? packageFamilyName);
}
