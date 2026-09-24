using System;
using System.Windows;

namespace ActivityTracker.Startup;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        _ = dispatcher.InvokeAsync(action);
    }
}
