using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Connection dialog (hardware-page spec §10): how Volco reaches the controller, one card per
/// transport (<see cref="UsbCard"/>, <see cref="WifiCard"/>, <see cref="SimulatorCard"/>). Opening a
/// card closes the others and switches nothing; only a button inside a card switches. In the card in
/// use the port edits apply at once (<see cref="Connection"/>); a card that isn't in use keeps its
/// edits until its button and drops them with the dialog. The Wi-Fi saves run through the run's
/// <see cref="WifiSwitch"/>, in the progress dialog. Opened only with a clean controller draft
/// (<see cref="HardwareViewModel.OpenConnection"/>). UI thread; events are posted.
/// </summary>
public sealed partial class ConnectionDialogViewModel : ObservableObject, IDisposable
{
    private readonly Action _close;
    private bool _disposed;

    /// <param name="wifi">The run's Wi-Fi switch; null: the Wi-Fi card can't save.</param>
    /// <param name="addresses">This PC's addresses (<see cref="LocalAddresses.Read"/>), read once.</param>
    /// <param name="open">The card the dialog opens on.</param>
    /// <param name="close">Closes the dialog (Close, Escape, a click outside).</param>
    /// <param name="progress">The window's progress dialog, which shows the Wi-Fi saves.</param>
    public ConnectionDialogViewModel(ControllerViewModel controller, WifiSwitch? wifi, Func<IReadOnlyList<LocalAddress>> addresses,
                                     ConnectionCardKind open, Action close, ProgressDialogViewModel? progress = null)
    {
        Controller = controller;
        WifiSwitch = wifi;
        Progress = progress;
        _close = close;
        UsbCard = new UsbCardViewModel(this);
        WifiCard = new WifiCardViewModel(this, addresses());
        SimulatorCard = new SimulatorCardViewModel(this);
        Cards = new ConnectionCardViewModel[] { UsbCard, WifiCard, SimulatorCard };
        Open(open);
        Connection.PropertyChanged += OnConnectionChanged;
        Controller.PropertyChanged += OnControllerChanged;
        if (wifi is not null) wifi.BusyChanged += OnWifiBusyChanged;
    }

    public string Title => "How Volco reaches the controller";

    /// <summary>The transport, COM port and ports Volco listens on (Volco's own settings).</summary>
    public ConnectionViewModel Connection => Controller.Connection;

    internal ControllerViewModel Controller { get; }

    internal WifiSwitch? WifiSwitch { get; }

    internal ProgressDialogViewModel? Progress { get; }

    internal IUiDispatcher Ui => Controller.Ui;

    public UsbCardViewModel UsbCard { get; }
    public WifiCardViewModel WifiCard { get; }
    public SimulatorCardViewModel SimulatorCard { get; }

    /// <summary>USB cable, Wi-Fi, Simulator, top to bottom.</summary>
    public IReadOnlyList<ConnectionCardViewModel> Cards { get; }

    /// <summary>The card of <paramref name="type"/>.</summary>
    public static ConnectionCardKind KindOf(ConnectionType type) => type switch
    {
        ConnectionType.Udp => ConnectionCardKind.Wifi,
        ConnectionType.WebSocket => ConnectionCardKind.Simulator,
        _ => ConnectionCardKind.Usb,
    };

    /// <summary>The card of the transport Volco uses now: it says "In use".</summary>
    public ConnectionCardKind InUse => KindOf(Connection.ConnectionType);

    /// <summary>Opens <paramref name="kind"/>'s card and closes the others; switches nothing.</summary>
    public void Open(ConnectionCardKind kind)
    {
        foreach (var card in Cards) card.IsOpen = card.Kind == kind;
    }

    /// <summary>Close: the dialog goes, with the edits of the cards not in use.</summary>
    [RelayCommand]
    private void Close() => _close();

    /// <summary>The transport changed under the dialog (a card's button, or the Wi-Fi switch): the
    /// config as it is now shows (it saves after its debounce), then every card follows.</summary>
    internal void TransportEdited()
    {
        if (_disposed) return;
        Connection.SyncFromConfig();
        Refresh();
    }

    private void Refresh()
    {
        foreach (var card in Cards) card.Refresh();
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed) Refresh();
    }

    // The mirror (the Wi-Fi summary, the fields' base) and whether the controller is ready.
    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed) WifiCard.Refresh();
    }

    private void OnWifiBusyChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (!_disposed) Refresh();
    });

    /// <summary>The dialog closed: it stops following the connection and the controller. A Wi-Fi
    /// operation it started goes on (the run's <see cref="WifiSwitch"/> owns it).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connection.PropertyChanged -= OnConnectionChanged;
        Controller.PropertyChanged -= OnControllerChanged;
        if (WifiSwitch is not null) WifiSwitch.BusyChanged -= OnWifiBusyChanged;
        foreach (var card in Cards) card.Dispose();
    }

    internal bool IsDisposed => _disposed;
}
