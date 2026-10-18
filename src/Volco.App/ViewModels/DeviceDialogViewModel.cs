using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Volco.App.ViewModels;

/// <summary>An open dialog that shows device names: it re-reads them when the device dialog over it
/// closes.</summary>
internal interface IDeviceNamesListener
{
    void DeviceNamesChanged();
}

/// <summary>
/// The device dialog (controls-page spec §8): one device's nickname and screen icon, opened from a
/// device row of the target dialog or a device picker of the action dialog. The fields are
/// <paramref name="device"/>'s, which save as they are edited; Done only closes.
/// </summary>
public sealed partial class DeviceDialogViewModel : ObservableObject, IDisposable
{
    private readonly Action _close;

    public DeviceItemViewModel Device { get; }

    /// <summary>The dialog opened from "Icon": the icon grid has the focus, else the nickname box.</summary>
    public bool FocusIcon { get; }

    /// <summary>The device's display name, which follows the nickname as it is typed.</summary>
    public string Title => Device.DisplayName;

    public DeviceDialogViewModel(DeviceItemViewModel device, bool focusIcon, Action close)
    {
        Device = device;
        FocusIcon = focusIcon;
        _close = close;
        Device.PropertyChanged += OnDeviceChanged;
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceItemViewModel.DisplayName)) OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private void Done() => _close();

    [RelayCommand]
    private void Close() => _close();

    /// <summary>Forgets the unplugged, unused device; there is nothing left to edit.</summary>
    [RelayCommand]
    private void Forget()
    {
        if (!Device.CanForget) return;
        Device.ForgetCommand.Execute(null);
        _close();
    }

    public void Dispose() => Device.PropertyChanged -= OnDeviceChanged;
}
