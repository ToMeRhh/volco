using Microsoft.UI.Dispatching;
using Volco.App.ViewModels;

namespace Volco.UI;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by a WinUI <see cref="DispatcherQueue"/>. Never blocks the
/// caller: runtime events can arrive on the audio thread or while a transport lock is held.
/// </summary>
internal sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action) => _queue.TryEnqueue(() => action());
}
