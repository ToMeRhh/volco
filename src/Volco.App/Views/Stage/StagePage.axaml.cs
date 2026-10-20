using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Hardware;
using Volco.Core.Input;

namespace Volco.App.Views.Stage;

/// <summary>
/// The stage: the drawing and the side panel, decorated for Controls or Hardware by
/// <see cref="Mode"/> (spec hardware-page.md §3.3). Bindings do the work; the code-behind selects a tile on a click,
/// Enter or Space, moves the focus between tiles with the arrow keys (spec §4.5), closes the
/// dialog on Escape or a click on the scrim (the device dialog over it first), runs the mode tabs (StagePage.ModeTabs.cs), drag
/// to swap (<see cref="TileSwapDrag"/>) and the tiles' "Swap with" menu.
/// </summary>
public partial class StagePage : UserControl
{
    public StagePage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        var overlay = this.FindControl<Border>("DialogOverlay")!;
        overlay.PointerPressed += OnOverlayPressed;
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (overlay.IsVisible) OnDialogOpened();
            else OnDialogClosed();
        };
        var deviceOverlay = this.FindControl<Border>("DeviceOverlay")!;
        deviceOverlay.PointerPressed += OnDeviceOverlayPressed;
        deviceOverlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (deviceOverlay.IsVisible) OnDeviceDialogOpened();
            else OnDeviceDialogClosed();
        };
        var compareOverlay = this.FindControl<Border>("CompareOverlay")!;
        compareOverlay.PointerPressed += OnCompareOverlayPressed;
        InitModeTabs();
        InitStage();
        // Drag to swap is a Controls thing: on Hardware the grips are faded out and inactive.
        SwapDrag = TileSwapDrag.Attach(_case, () => Mode == StageMode.Controls ? Vm : null);
        // Tunnel, so "Swap with" is filled before the tile's own handler opens its menu.
        AddHandler(ContextRequestedEvent, OnTileContextRequested, RoutingStrategies.Tunnel);
    }

    public static readonly StyledProperty<StageMode> ModeProperty =
        AvaloniaProperty.Register<StagePage, StageMode>(nameof(Mode));

    public static readonly StyledProperty<Control?> HardwareSidePanelProperty =
        AvaloniaProperty.Register<StagePage, Control?>(nameof(HardwareSidePanel));

    /// <summary>Which page the stage decorates (spec hardware-page.md §3.3). Switching crossfades
    /// the title, the tiles' captions, the mode frame and the side panel; no tile moves.</summary>
    public StageMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly StyledProperty<HardwareViewModel?> HardwareProperty =
        AvaloniaProperty.Register<StagePage, HardwareViewModel?>(nameof(Hardware));

    /// <summary>The Hardware page's view model: on Hardware the stage draws its controls (the
    /// draft's, spec §5.1) and its bus chips. Null draws the Controls items in both modes.</summary>
    public HardwareViewModel? Hardware
    {
        get => GetValue(HardwareProperty);
        set => SetValue(HardwareProperty, value);
    }

    /// <summary>The side panel's content on Hardware; Controls shows the inspector.</summary>
    public Control? HardwareSidePanel
    {
        get => GetValue(HardwareSidePanelProperty);
        set => SetValue(HardwareSidePanelProperty, value);
    }

    private Border _case = null!;
    private Control _drawing = null!;
    private TransitioningContentControl _chooserHost = null!;
    private Border _compareOverlay = null!;
    private ContentControl _compareHost = null!;
    private SetupChooser? _chooser;
    private TransitioningContentControl _titleHost = null!;
    private TransitioningContentControl _sidePanelHost = null!;
    private Control _inspector = null!;
    private ItemsControl _sliderList = null!;
    private ItemsControl _buttonList = null!;
    private readonly TextBlock _controlsTitle = new() { Name = "PageTitle", Classes = { "Title" }, Text = "Controls" };
    private readonly TextBlock _hardwareTitle = new() { Name = "PageTitle", Classes = { "Title" }, Text = "Hardware" };

    private void InitStage()
    {
        _case = this.FindControl<Border>("Case")!;
        _drawing = this.FindControl<StackPanel>("Drawing")!;
        _chooserHost = this.FindControl<TransitioningContentControl>("ChooserHost")!;
        _chooserHost.PageTransition = null;
        _compareOverlay = this.FindControl<Border>("CompareOverlay")!;
        _compareHost = this.FindControl<ContentControl>("CompareHost")!;
        _titleHost = this.FindControl<TransitioningContentControl>("TitleHost")!;
        _sidePanelHost = this.FindControl<TransitioningContentControl>("SidePanelHost")!;
        _inspector = this.FindControl<ContentControl>("InspectorContent")!;
        _sliderList = this.FindControl<ItemsControl>("SliderList")!;
        _buttonList = this.FindControl<ItemsControl>("ButtonList")!;
        _titleHost.PageTransition = null;
        _sidePanelHost.PageTransition = null;
        _titleHost.Content = _controlsTitle;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_case is null) return;   // still loading
        if (change.Property == ModeProperty) ApplyMode();
        else if (change.Property == HardwareProperty)
        {
            if (change.OldValue is HardwareViewModel old)
            {
                old.Sliders.CollectionChanged -= OnDrawnChanged;
                old.Buttons.CollectionChanged -= OnDrawnChanged;
                old.PropertyChanged -= OnHardwarePropertyChanged;
            }
            if (change.NewValue is HardwareViewModel hw)
            {
                hw.Sliders.CollectionChanged += OnDrawnChanged;
                hw.Buttons.CollectionChanged += OnDrawnChanged;
                hw.PropertyChanged += OnHardwarePropertyChanged;
            }
            _chooser = null;
            UpdateTileSources();
            UpdateChooser();
            UpdateCompare();
        }
        else if (change.Property == HardwareSidePanelProperty && Mode == StageMode.Hardware)
        {
            _sidePanelHost.PageTransition = StageTransitions.PageTransition();
            _sidePanelHost.Content = HardwareSidePanel;
        }
    }

    /// <summary>Swaps the decorations: the "hardware" class (styles fade the tiles' layers and the
    /// mode frame), the title word and the side panel's content.</summary>
    private void ApplyMode()
    {
        var hardware = Mode == StageMode.Hardware;
        StageTransitions.Prepare(_case);
        Classes.Set("hardware", hardware);
        _case.Classes.Set("hardware", hardware);
        _titleHost.PageTransition = StageTransitions.PageTransition();
        _titleHost.Content = hardware ? _hardwareTitle : _controlsTitle;
        _sidePanelHost.PageTransition = StageTransitions.PageTransition();
        _sidePanelHost.Content = hardware ? HardwareSidePanel : _inspector;
        UpdateTileSources();
        UpdateChooser();
        UpdateCompare();
    }

    private void OnHardwarePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HardwareViewModel.ShowChooser)) UpdateChooser();
        else if (e.PropertyName is nameof(HardwareViewModel.IsCompareOpen) or nameof(HardwareViewModel.Compare)) UpdateCompare();
    }

    /// <summary>On Hardware, the setup chooser takes the case's rectangle (spec §3.3, §7.1): the
    /// drawing fades out and keeps its measure, out of pointer and keyboard reach, and the chooser
    /// crossfades in over it.</summary>
    private void UpdateChooser()
    {
        var show = Mode == StageMode.Hardware && Hardware is { ShowChooser: true };
        if (show && Hardware is { } hw) _chooser ??= new SetupChooser { DataContext = hw.Chooser };
        if (StageTransitions.Enabled && _drawing.Transitions is not { Count: > 0 })
            _drawing.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = StageTransitions.Duration },
            };
        else if (!StageTransitions.Enabled) _drawing.Transitions = null;
        _drawing.Opacity = show ? 0 : 1;
        _drawing.IsHitTestVisible = !show;
        _drawing.IsEnabled = !show;
        var content = show ? _chooser : null;
        if (ReferenceEquals(_chooserHost.Content, content)) return;
        _chooserHost.PageTransition = StageTransitions.PageTransition();
        _chooserHost.Content = content;
    }

    /// <summary>Compare's card over the stage while Hardware has it open; the focus moves into it.</summary>
    private void UpdateCompare()
    {
        var hw = Mode == StageMode.Hardware ? Hardware : null;
        var open = hw is { IsCompareOpen: true, Compare: not null };
        _compareHost.Content = open ? hw!.Compare : null;
        if (_compareOverlay.IsVisible == open) return;
        _compareOverlay.IsVisible = open;
        if (!open) return;
        var card = this.FindControl<Border>("CompareCard")!;
        Dispatcher.UIThread.Post(() =>
        {
            if (!card.IsEffectivelyVisible || card.IsKeyboardFocusWithin) return;
            card.UpdateLayout();
            // While the read runs nothing in it can take the focus: the card takes it, so Escape works.
            var first = card.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            if (first is not null) first.Focus(NavigationMethod.Tab);
            else card.Focus();
        });
    }

    /// <summary>A click on Compare's scrim closes it.</summary>
    private void OnCompareOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || Hardware is not { IsCompareOpen: true } hw) return;
        hw.CloseCompare();
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_case is not null) UpdateTileSources();
    }

    private void OnDrawnChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateTileSources();

    /// <summary>The tiles drawn: on Hardware the draft's controls (spec §5.1), else the Controls
    /// items. While the draft's are the Controls items in order the stage keeps drawing those, so
    /// switching modes rebuilds no tile.</summary>
    private void UpdateTileSources()
    {
        var vm = Vm;
        var hw = Mode == StageMode.Hardware ? Hardware : null;
        Show(_sliderList, vm is null ? null
             : hw is not null && !hw.Sliders.SequenceEqual(vm.Sliders.Items) ? hw.Sliders : vm.Sliders.Items);
        Show(_buttonList, vm is null ? null
             : hw is not null && !hw.Buttons.SequenceEqual(vm.Buttons.Items) ? hw.Buttons : vm.Buttons.Items);

        static void Show(ItemsControl list, IEnumerable? items)
        {
            if (!ReferenceEquals(list.ItemsSource, items)) list.ItemsSource = items;
        }
    }

    /// <summary>Drag a tile's grip onto another tile of its kind to swap them (spec §2.9–10).</summary>
    internal TileSwapDrag SwapDrag { get; }

    /// <summary>Fills the tile menu's "Swap with" with the other controls of the tile's kind
    /// (spec §11); a right-click, Shift+F10 or the Menu key opens it.</summary>
    private void OnTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TileOf(e.Source as Visual) is not { ContextMenu: { } menu } tile || Vm is not { } vm) return;
        var swapWith = menu.Items.OfType<MenuItem>().First();
        if (Mode == StageMode.Hardware)
        {
            // The menu only has "Swap with", which Hardware doesn't offer.
            swapWith.ItemsSource = null;
            swapWith.IsEnabled = false;
            e.Handled = true;
            return;
        }
        if (tile.DataContext is not ObservableObject item) return;
        var missing = item is SliderItemViewModel { IsMissing: true } or ButtonItemViewModel { IsMissing: true };
        var choices = missing ? Array.Empty<SwapChoice>() : vm.SwapChoices(item);
        swapWith.ItemsSource = choices.Select(choice =>
        {
            var entry = new MenuItem { Header = choice.Label };
            entry.Click += (_, _) => vm.Swap(item, choice.Target);
            return entry;
        }).ToList();
        swapWith.IsEnabled = choices.Count > 0;
    }

    // What had the focus when the dialog opened, and the dialog.
    private IInputElement? _focusBeforeDialog;
    private object? _openDialog;

    /// <summary>Focus moves into the dialog, so Escape reaches the page (a dialog may focus its
    /// own first field, such as the action dialog's search box).</summary>
    private void OnDialogOpened()
    {
        _focusBeforeDialog = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _openDialog = Vm?.Dialog;
        var card = this.FindControl<Border>("DialogCard")!;
        card.Classes.Set("Narrow", _openDialog is TargetDialogViewModel);
        Dispatcher.UIThread.Post(() =>
        {
            if (!card.IsEffectivelyVisible || card.IsKeyboardFocusWithin) return;
            card.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
                ?.Focus(NavigationMethod.Tab);
        });
    }

    /// <summary>The focus goes back to the row that opened the dialog (spec §11).</summary>
    private void OnDialogClosed()
    {
        var opener = _openDialog switch
        {
            ActionDialogViewModel action => ActionRowButton(action.Gesture),
            TargetDialogViewModel => InspectorButton("ControlsRow"),
            _ => null,
        };
        var before = _focusBeforeDialog;
        _focusBeforeDialog = null;
        _openDialog = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (opener is { IsEffectivelyVisible: true }) opener.Focus(NavigationMethod.Tab);
            else if (before is InputElement element && TopLevel.GetTopLevel(element) is not null) element.Focus(NavigationMethod.Tab);
        });
    }

    // What had the focus when the device dialog opened (the ⋯ button), and its row.
    private IInputElement? _focusBeforeDeviceDialog;
    private (string? Name, object? Row) _deviceOpener;

    /// <summary>The focus moves into the device dialog: its nickname box with the text selected, or
    /// after "Icon" the selected icon (else the first).</summary>
    private void OnDeviceDialogOpened()
    {
        var before = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _focusBeforeDeviceDialog = before;
        _deviceOpener = ((before as Control)?.Name, (before as Control)?.DataContext);
        var focusIcon = Vm?.DeviceDialog?.FocusIcon == true;
        var card = this.FindControl<Border>("DeviceCard")!;
        Dispatcher.UIThread.Post(() =>
        {
            if (!card.IsEffectivelyVisible) return;
            card.UpdateLayout();   // the dialog's view is created by the layout pass
            if (focusIcon)
            {
                var icons = card.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "IconChoice").ToList();
                (icons.FirstOrDefault(b => b.Classes.Contains("selected")) ?? icons.FirstOrDefault())?.Focus(NavigationMethod.Tab);
            }
            else if (card.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "DeviceNameBox") is { } box)
            {
                box.Focus(NavigationMethod.Tab);
                box.SelectAll();
            }
        });
    }

    /// <summary>The focus goes back to the ⋯ button that opened the device dialog. A target dialog
    /// row is rebuilt when a name changes, so its new ⋯ button is found by the row's key.</summary>
    private void OnDeviceDialogClosed()
    {
        var before = _focusBeforeDeviceDialog;
        var (name, row) = _deviceOpener;
        _focusBeforeDeviceDialog = null;
        _deviceOpener = default;
        Dispatcher.UIThread.Post(() =>
        {
            if (!HasDialogOpen()) return;
            if (before is InputElement { IsEffectivelyVisible: true } element && TopLevel.GetTopLevel(element) is not null)
            {
                element.Focus(NavigationMethod.Tab);
                return;
            }
            var card = this.FindControl<Border>("DialogCard")!;
            var again = name is null ? null : card.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name
                && (row is not TargetItem item || (b.DataContext is TargetItem other && other.Key == item.Key)));
            if (again is not null) again.Focus(NavigationMethod.Tab);
            else card.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)?.Focus(NavigationMethod.Tab);
        });
    }

    private bool HasDialogOpen() => Vm?.HasDialog == true;

    private Button? InspectorButton(string name)
        => this.FindControl<Border>("Inspector")?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name);

    /// <summary>The inspector's row button for a gesture of the selected button.</summary>
    private Button? ActionRowButton(PressType gesture)
    {
        var row = gesture switch { PressType.Double => "ActionRow_Double", PressType.Long => "ActionRow_Hold", _ => "ActionRow_Press" };
        return this.FindControl<Border>("Inspector")?.GetVisualDescendants().OfType<ContentControl>()
            .FirstOrDefault(c => c.Name == row)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "RowButton");
    }

    public StagePage(ControlsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private ControlsViewModel? Vm => DataContext as ControlsViewModel;

    /// <summary>The tile <paramref name="visual"/> is in, or null.</summary>
    private static Control? TileOf(Visual? visual)
    {
        for (var v = visual; v is not null; v = v.GetVisualParent())
            if (v is SliderTile or ButtonTile) return (Control)v;
        return null;
    }

    /// <summary>Every tile in drawing order: the sliders, then the buttons.</summary>
    internal List<Control> Tiles()
        => this.GetVisualDescendants().Where(v => v is SliderTile or ButtonTile).Cast<Control>().ToList();

    private void Select(Control tile)
    {
        if (Vm is { } vm && tile.DataContext is ObservableObject item) vm.Select(item);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TileOf(e.Source as Visual) is not { } tile) return;
        Select(tile);
        tile.Focus(NavigationMethod.Pointer);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_compareOverlay.IsVisible && Hardware is { } hw)
        {
            // Compare is modal: Escape closes it, and no key reaches the tiles under it.
            if (e.Key == Key.Escape)
            {
                hw.CloseCompare();
                e.Handled = true;
            }
            return;
        }
        if (Vm is not { } vm) return;
        if (vm.HasDeviceDialog)
        {
            // The device dialog is over the other one: Escape closes it first.
            if (e.Key != Key.Escape) return;
            vm.CloseDeviceDialog();
            e.Handled = true;
            return;
        }
        if (vm.HasDialog)
        {
            if (e.Key != Key.Escape) return;
            // Escape first closes an open dropdown in the dialog; only the next one closes the dialog.
            var open = this.FindControl<Border>("DialogCard")!.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.IsDropDownOpen);
            if (open is not null) open.IsDropDownOpen = false;
            else vm.CloseDialog();
            e.Handled = true;
            return;
        }
        if (TileOf(e.Source as Visual) is not { } tile) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                Select(tile);
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                MoveFocus(tile, -1);
                e.Handled = true;
                break;
            case Key.Right or Key.Down:
                MoveFocus(tile, +1);
                e.Handled = true;
                break;
        }
    }

    private void MoveFocus(Control from, int step)
    {
        var tiles = Tiles();
        var next = tiles.IndexOf(from) + step;
        if (next < 0 || next >= tiles.Count) return;
        tiles[next].Focus(NavigationMethod.Directional);
        tiles[next].BringIntoView();
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || Vm is not { HasDialog: true } vm) return;   // a click inside the card
        vm.CloseDialog();
        e.Handled = true;
    }

    /// <summary>A click on the device dialog's scrim closes only the device dialog.</summary>
    private void OnDeviceOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || Vm is not { HasDeviceDialog: true } vm) return;
        vm.CloseDeviceDialog();
        e.Handled = true;
    }
}
