namespace ActivityTracker.Models;

public enum AppIdentitySource//用户识别
{
    Persisted,
    UserOverride,
    ApplicationUserModelId,
    PackageFamilyName,
    VersionResource,
    ExecutablePath,
    ProcessName
}

public sealed record AppIdentity(
    string AppId,
    string AppName,
    AppIdentitySource Source)
{
    public static AppIdentity Idle { get; } = new(
        "system:idle",
        "用户空闲",
        AppIdentitySource.Persisted);
}
