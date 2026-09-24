using System;

namespace ActivityTracker.Startup;

public interface IUiDispatcher
{
    void Post(Action action);
}
