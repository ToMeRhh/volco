using CommunityToolkit.Mvvm.ComponentModel;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Connection dialog (hardware-page spec §10): how Volco reaches the controller. The segment
/// picks a tab only; a tab that isn't the transport in use offers "Use …", which switches Volco's
/// transport at once. The transport and its ports are Volco's own settings (<see cref="Connection"/>);
/// the controller's Wi-Fi fields edit the draft (<see cref="Controller"/>), which the save bar sends.
/// </summary>
public sealed partial class ConnectionDialogViewModel : ObservableObject, IDisposable
{
    private readonly Action _close;
    private bool _disposed;

    /// <param name="wifi">Open on the Wi-Fi tab, else on the current transport's.</param>
    public ConnectionDialogViewModel(ControllerViewModel controller, bool wifi, Action close)
    {
        Controller = controller;
        _close = close;
        _tab = wifi ? 1 : controller.Connection.ConnectionTypeIndex;
        controller.Connection.PropertyChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_disposed && e.PropertyName is nameof(ConnectionViewModel.ConnectionTypeIndex)) RaiseUse();
    }

    /// <summary>The transport, COM port and ports Volco listens on.</summary>
    public ConnectionViewModel Connection => Controller.Connection;

    /// <summary>The controller's Wi-Fi fields, on the draft.</summary>
    public ControllerViewModel Controller { get; }

    /// <summary>The shown tab: 0 USB, 1 Wi-Fi, 2 Simulator (the transport indexes).</summary>
    [ObservableProperty] private int _tab;

    partial void OnTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsUsbTab));
        OnPropertyChanged(nameof(IsWifiTab));
        OnPropertyChanged(nameof(IsSimulatorTab));
        RaiseUse();
    }

    public bool IsUsbTab { get => Tab == 0; set { if (value) Tab = 0; } }
    public bool IsWifiTab { get => Tab == 1; set { if (value) Tab = 1; } }
    public bool IsSimulatorTab { get => Tab == 2; set { if (value) Tab = 2; } }

    /// <summary>The shown tab is the transport in use: it says "In use".</summary>
    public bool TabInUse => Tab == Connection.ConnectionTypeIndex;

    /// <summary>The shown tab is not in use: it offers "Use …".</summary>
    public bool CanUseTab => !TabInUse;

    private void RaiseUse()
    {
        OnPropertyChanged(nameof(TabInUse));
        OnPropertyChanged(nameof(CanUseTab));
    }

    /// <summary>"Use USB" / "Use Wi-Fi" / "Use the simulator": switches Volco's transport to the shown tab.</summary>
    public void UseTab() => Connection.ConnectionTypeIndex = Tab;

    /// <summary>Done: closes the dialog.</summary>
    public void Close() => _close();

    /// <summary>The dialog closed: it stops following the connection settings.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connection.PropertyChanged -= OnConnectionChanged;
    }
}
