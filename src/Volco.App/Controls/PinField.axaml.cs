using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Controls;

/// <summary>
/// The pin field (hardware-page spec §6.3) over a <see cref="PinFieldViewModel"/>. A click opens
/// it with the focus in the text box; there Up/Down move, Enter picks and Escape closes without
/// change, as does a click outside the field and its list. The control has class "invalid" while
/// the field has a problem.
/// </summary>
public partial class PinField : UserControl
{
    private readonly Button _valueButton;
    private readonly TextBox _queryBox;
    private readonly Popup _popup;
    private readonly ItemsControl _rows;
    private PinFieldViewModel? _vm;
    private TopLevel? _watched;

    public PinField()
    {
        AvaloniaXamlLoader.Load(this);
        _valueButton = this.FindControl<Button>("ValueButton")!;
        _queryBox = this.FindControl<TextBox>("QueryBox")!;
        _popup = this.FindControl<Popup>("ListPopup")!;
        _rows = this.FindControl<ItemsControl>("RowList")!;
        _queryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
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
        WatchOutsideClicks(_vm?.IsOpen == true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _vm?.Cancel();
        WatchOutsideClicks(false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(PinFieldViewModel.HasError):
                Classes.Set("invalid", _vm.HasError);
                break;
            case nameof(PinFieldViewModel.IsOpen):
                WatchOutsideClicks(_vm.IsOpen);
                if (_vm.IsOpen) Dispatcher.UIThread.Post(() => _queryBox.Focus(NavigationMethod.Tab));
                else if (_queryBox.IsFocused || _queryBox.IsKeyboardFocusWithin)
                    Dispatcher.UIThread.Post(() => _valueButton.Focus(NavigationMethod.Tab));
                break;
            case nameof(PinFieldViewModel.Highlighted):
                if (_vm.Highlighted is { } row)
                    Dispatcher.UIThread.Post(() => _rows.ContainerFromItem(row)?.BringIntoView());
                break;
        }
    }

    private void ValueButton_Click(object? sender, RoutedEventArgs e) => _vm?.Open();

    private void Row_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PinRowViewModel row }) _vm?.Pick(row);
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Down:
                _vm.Move(+1);
                break;
            case Key.Up:
                _vm.Move(-1);
                break;
            case Key.Enter:
                _vm.Commit();
                break;
            case Key.Escape:
                _vm.Cancel();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>While open, a press anywhere but the field and its list closes it without change.</summary>
    private void WatchOutsideClicks(bool on)
    {
        var top = on ? TopLevel.GetTopLevel(this) : null;
        if (ReferenceEquals(top, _watched)) return;
        _watched?.RemoveHandler(PointerPressedEvent, OnAnyPointerPressed);
        _watched = top;
        _watched?.AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (this.IsVisualAncestorOf(source) || ReferenceEquals(source, this)) return;
        if (_popup.Child is { } list && (ReferenceEquals(source, list) || list.IsVisualAncestorOf(source))) return;
        _vm?.Cancel();
    }
}
