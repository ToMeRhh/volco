using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

public partial class ControllerPage : UserControl
{
    public ControllerPage()
    {
        AvaloniaXamlLoader.Load(this);
        // Escape, or a click on the scrim outside the card, closes the open dialog (and ends a test).
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("DialogOverlay")!.PointerPressed += OnOverlayPressed;
    }

    public ControllerPage(ControllerViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private ControllerViewModel? Vm => DataContext as ControllerViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { HasDialog: true } vm) return;
        vm.CloseDialogCommand.Execute(null);
        e.Handled = true;
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || Vm is not { HasDialog: true } vm) return;   // a click inside the card
        vm.CloseDialogCommand.Execute(null);
        e.Handled = true;
    }
}
