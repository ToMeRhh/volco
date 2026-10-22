using Volco.Core.Controller;
using Volco.Core.Firmware;

namespace Volco.App;

/// <summary>Holds an Exit while a firmware write runs (an interrupted write leaves the controller
/// without firmware until Install runs again) or a config save is in flight (the controller would
/// be left mid-write or restarting with no one to confirm it), then exits once it ends. UI thread only.</summary>
internal sealed class ExitGate(Func<bool> isBusy)
{
    private Action? _deferred;

    /// <summary>The run's gate: busy while <paramref name="firmware"/> writes or probes, or while
    /// <paramref name="config"/> writes the controller's setup; their changes reach
    /// <see cref="OnBusyChanged"/> through <paramref name="post"/> (onto the UI thread).</summary>
    public static ExitGate For(FirmwareService firmware, ControllerConfigService config, Action<Action> post)
    {
        var gate = new ExitGate(() => firmware.IsBusy || config.Operation == ConfigOperation.Writing);
        firmware.BusyChanged += (_, _) => post(gate.OnBusyChanged);
        config.OperationChanged += (_, _) => post(gate.OnBusyChanged);
        return gate;
    }

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
