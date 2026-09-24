using System;
using System.Threading;

namespace ActivityTracker.Configuration;

public sealed class SettingsService
{
    private readonly object _sync = new();
    private readonly SettingsRepository _repository;
    private AppSettings _current;

    public AppSettings Current => Volatile.Read(ref _current);
    public event Action<AppSettings>? Changed;

    public SettingsService(SettingsRepository repository)
    {
        _repository = repository;
        _current = _repository.Load();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _repository.Save(settings);
            Volatile.Write(ref _current, settings);
        }

        Changed?.Invoke(settings);
    }

    public AppSettings Reload()
    {
        AppSettings settings;

        lock (_sync)
        {
            settings = _repository.Load();
            Volatile.Write(ref _current, settings);
        }

        Changed?.Invoke(settings);
        return settings;
    }
}
