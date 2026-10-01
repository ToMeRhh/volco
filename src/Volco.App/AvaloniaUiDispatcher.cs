using Avalonia.Threading;
using Volco.App.ViewModels;

namespace Volco.App;

/// <summary>
/// <see cref="IUiDispatcher"/> over <see cref="Dispatcher.UIThread"/>. Always Post, never Invoke:
/// runtime events can arrive while a transport lock is held (carried rule b).
/// </summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
