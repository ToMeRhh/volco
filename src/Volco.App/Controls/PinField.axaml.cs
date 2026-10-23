using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Controls;

/// <summary>
/// The pin field (hardware-page spec §6.3) over a <see cref="PinFieldViewModel"/>: a click opens the
/// pin dialog, which the settings window shows over everything (<see cref="HardwareViewModel.PinDialog"/>).
/// A field that leaves the screen (another tile is selected) closes its dialog. The control has class
/// "invalid" while the field has a problem.
/// </summary>
public partial class PinField : UserControl
{
    private PinFieldViewModel? _vm;

    public PinField()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Cancel();
        }
        _vm = DataContext as PinFieldViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        Classes.Set("invalid", _vm?.HasError == true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _vm?.Cancel();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is not null && e.PropertyName == nameof(PinFieldViewModel.HasError)) Classes.Set("invalid", _vm.HasError);
    }

    private void ValueButton_Click(object? sender, RoutedEventArgs e) => _vm?.Open();
}
