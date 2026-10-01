namespace Volco.App.ViewModels;

/// <summary>
/// Posts work to the UI thread. ViewModel-internal, NOT a platform seam: it exists so the
/// ViewModels carry no UI-framework types. Runtime events fire on arbitrary threads (audio,
/// transport, timers), so every handler marshals through this and must never block on it.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
