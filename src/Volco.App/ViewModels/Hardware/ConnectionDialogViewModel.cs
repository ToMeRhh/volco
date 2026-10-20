namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Connection dialog (hardware-page spec §10): how Volco reaches the controller. The
/// transport and its ports are Volco's own settings (<see cref="Connection"/>, stored at once);
/// the controller's Wi-Fi fields edit the draft (<see cref="Controller"/>), which the save bar sends.
/// </summary>
public sealed class ConnectionDialogViewModel
{
    private readonly Action _close;

    public ConnectionDialogViewModel(ControllerViewModel controller, Action close)
    {
        Controller = controller;
        _close = close;
    }

    /// <summary>The transport, COM port and ports Volco listens on.</summary>
    public ConnectionViewModel Connection => Controller.Connection;

    /// <summary>The controller's Wi-Fi fields, on the draft.</summary>
    public ControllerViewModel Controller { get; }

    /// <summary>Done: closes the dialog.</summary>
    public void Close() => _close();
}
