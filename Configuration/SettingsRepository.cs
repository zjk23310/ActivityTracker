using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ActivityTracker.Configuration;

public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _path;

    public string? LastRecoveryMessage { get; private set; }

    public SettingsRepository(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        lock (_sync)
        {
            LastRecoveryMessage = null;

            if (!File.Exists(_path))
            {
                var defaults = new AppSettings();
                SaveCore(defaults);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(_path, Encoding.UTF8);
                var settings =
                    JsonSerializer.Deserialize<AppSettings>(
                        json,
                        JsonOptions)
                    ?? throw new JsonException(
                        "设置文件内容为空。");

                var original =
                    JsonSerializer.Serialize(settings, JsonOptions);

                settings.Normalize();

                var normalized =
                    JsonSerializer.Serialize(settings, JsonOptions);

                if (!string.Equals(
                        original,
                        normalized,
                        StringComparison.Ordinal))
                {
                    SaveCore(settings);
                    LastRecoveryMessage =
                        "设置文件包含缺失或越界值，已自动修正。";
                }

                return settings;
            }
            catch (JsonException ex)
            {
                return RecoverInvalidFile(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return RecoverInvalidFile(ex.Message);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            settings.Normalize();
            SaveCore(settings);
        }
    }

    private AppSettings RecoverInvalidFile(string reason)
    {
        var directory =
            Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "设置文件路径没有父目录。");

        Directory.CreateDirectory(directory);

        var backupPath = Path.Combine(
            directory,
            $"settings.invalid-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");

        File.Move(_path, backupPath);

        var defaults = new AppSettings();
        SaveCore(defaults);

        LastRecoveryMessage =
            $"设置文件无法读取，已备份为 {backupPath}。原因：{reason}";

        return defaults;
    }

    private void SaveCore(AppSettings settings)
    {
        var directory =
            Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "设置文件路径没有父目录。");

        Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        var backupPath = _path + ".bak";
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        File.WriteAllText(
            temporaryPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(
                        temporaryPath,
                        _path,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(
                        temporaryPath,
                        _path,
                        overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
