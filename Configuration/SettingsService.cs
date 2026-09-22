using System;

namespace ActivityTracker.Configuration;

public sealed class SettingsService
{
    private readonly object _sync = new();
    private readonly SettingsRepository _repository;

    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public SettingsService(SettingsRepository repository)
    {
        _repository = repository;
        Current = _repository.Load();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _repository.Save(settings);
            Current = settings;
        }

        Changed?.Invoke(settings);
    }

    public AppSettings Reload()
    {
        AppSettings settings;

        lock (_sync)
        {
            settings = _repository.Load();
            Current = settings;
        }

        Changed?.Invoke(settings);
        return settings;
    }
}
