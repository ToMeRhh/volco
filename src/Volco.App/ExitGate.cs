namespace Volco.App;

/// <summary>Holds an Exit while a firmware write runs (an interrupted write leaves the controller
/// without firmware until Install runs again), then exits once it ends. UI thread only.</summary>
internal sealed class ExitGate(Func<bool> isBusy)
{
    private Action? _deferred;

    public bool ShouldDefer() => isBusy();

    public void Defer(Action exit) => _deferred ??= exit;

    public void OnBusyChanged()
    {
        if (_deferred is null || isBusy()) return;
        var exit = _deferred;
        _deferred = null;
        exit();
    }
}
