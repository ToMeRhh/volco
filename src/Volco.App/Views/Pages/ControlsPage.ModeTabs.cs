using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>
/// The mode tabs (spec §9): a click edits a mode (and renames it inline in edit mode), a
/// right-click or the context-menu key opens its menu, and <see cref="ModeTabReorder"/> moves
/// tabs while editing. The menu opens whenever MenuModeId is set (Add mode sets it too).
/// </summary>
public partial class ControlsPage
{
    private ItemsControl _modeTabs = null!;
    private ButtonsViewModel? _buttons;
    // The mode whose menu is showing and its name then, for the rename when it closes.
    private string? _menuShownFor;
    private string _menuNameShown = "";

    /// <summary>The mode menu (spec §9.2), a flyout on the tab whose id is MenuModeId.</summary>
    internal Flyout ModeMenu { get; private set; } = null!;
    internal ModeTabReorder TabReorder { get; private set; } = null!;

    private void InitModeTabs()
    {
        _modeTabs = this.FindControl<ItemsControl>("ModeTabs")!;
        ModeMenu = (Flyout)FlyoutBase.GetAttachedFlyout(_modeTabs)!;
        // The name generator declares MenuNameBox, MenuMakeLive and MenuCopyFrom; the page loads
        // with AvaloniaXamlLoader, so they are set here.
        var panel = (Control)ModeMenu.Content!;
        T Named<T>(string name) where T : Control
            => panel.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);
        MenuNameBox = Named<TextBox>("MenuNameBox");
        MenuMakeLive = Named<Button>("MenuMakeLive");
        MenuCopyFrom = Named<Button>("MenuCopyFrom");

        ModeMenu.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            MenuNameBox.Focus(NavigationMethod.Tab);
            MenuNameBox.SelectAll();
        });
        ModeMenu.Closed += (_, _) => OnMenuClosed();
        panel.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            ModeMenu.Hide();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        MenuMakeLive.Click += (_, _) =>
        {
            if (_menuShownFor is { } id) _buttons?.MakeLive(id);
            ModeMenu.Hide();
        };

        _modeTabs.AddHandler(Button.ClickEvent, OnTabClick, RoutingStrategies.Bubble);
        _modeTabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _modeTabs.AddHandler(KeyDownEvent, OnTabKeyDown, RoutingStrategies.Tunnel);
        _modeTabs.AddHandler(KeyUpEvent, OnTabKeyUp, RoutingStrategies.Tunnel);
        _modeTabs.AddHandler(LostFocusEvent, OnTabNameBoxLostFocus, RoutingStrategies.Bubble);
        TabReorder = ModeTabReorder.Attach(_modeTabs, () => _buttons);

        DataContextChanged += (_, _) => WatchButtons(Vm?.Buttons);
    }

    private void WatchButtons(ButtonsViewModel? buttons)
    {
        if (ReferenceEquals(buttons, _buttons)) return;
        if (_buttons is not null) _buttons.PropertyChanged -= OnButtonsChanged;
        _buttons = buttons;
        if (_buttons is not null) _buttons.PropertyChanged += OnButtonsChanged;
    }

    private void OnButtonsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ButtonsViewModel.MenuModeId):
                if (_buttons?.MenuModeId is not null) Dispatcher.UIThread.Post(ShowModeMenu, DispatcherPriority.Loaded);
                else if (ModeMenu.IsOpen) ModeMenu.Hide();
                break;
            case nameof(ButtonsViewModel.RenamingModeId):
                if (_buttons?.RenamingModeId is { } id) Dispatcher.UIThread.Post(() => FocusTabNameBox(id), DispatcherPriority.Loaded);
                break;
        }
    }

    private int TabIndexOf(string id) => _buttons?.Presets.ToList().FindIndex(t => t.Id == id) ?? -1;

    // ── The menu ──────────────────────────────────────────────────────────────

    /// <summary>Opens the menu of the mode MenuModeId names, anchored to its tab. Not while a
    /// dialog is open: the dialog edits a draft of one mode, which Copy actions from could replace.</summary>
    private void ShowModeMenu()
    {
        if (_buttons is not { MenuModeId: { } id } buttons) return;
        if (ModeMenu.IsOpen && _menuShownFor == id) return;
        _modeTabs.UpdateLayout();
        if (Vm is { HasDialog: true } || !_modeTabs.IsEffectivelyVisible
            || ModeTabReorder.TabAt(_modeTabs, TabIndexOf(id)) is not { } tab)
        {
            buttons.MenuModeId = null;
            return;
        }
        if (ModeMenu.IsOpen) ModeMenu.Hide();   // another mode's menu closes (and saves) first
        buttons.MenuModeId = id;                 // closing it cleared the id

        _menuShownFor = id;
        _menuNameShown = buttons.PresetName(id) ?? "";
        MenuNameBox.Text = _menuNameShown;
        MenuMakeLive.IsVisible = buttons.LivePresetId != id;
        var others = buttons.PresetChoices().Where(p => p.Id != id).ToList();
        MenuCopyFrom.IsVisible = others.Count > 0;
        ((MenuFlyout)MenuCopyFrom.Flyout!).ItemsSource = others.Select(p =>
        {
            var item = new MenuItem { Header = p.Name, Tag = p.Id };
            item.Click += (_, _) =>
            {
                buttons.CopyActions(p.Id, id);
                ModeMenu.Hide();
            };
            return item;
        }).ToList();
        ModeMenu.ShowAt(tab);
    }

    /// <summary>Closing the menu (Enter, Escape, a click outside) saves its name.</summary>
    private void OnMenuClosed()
    {
        var id = _menuShownFor;
        _menuShownFor = null;
        if (_buttons is not { } buttons) return;
        // Only an edited name is saved (empty keeps the old one).
        var name = MenuNameBox.Text ?? "";
        if (id is not null && name != _menuNameShown) buttons.RenameMode(id, name);
        buttons.MenuModeId = null;
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    private static PresetTabViewModel? TabModel(object? source)
        => (source as StyledElement)?.DataContext as PresetTabViewModel;

    /// <summary>The nearest ancestor-or-self of <paramref name="source"/> of type <typeparamref name="T"/>
    /// with that name, or null.</summary>
    private static T? Up<T>(object? source, string name) where T : Control
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is T c && c.Name == name) return c;
        return null;
    }

    /// <summary>A click on a tab edits its mode (its command); in edit mode it also renames it.</summary>
    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not RadioButton { Name: "ModeTab" } tab || _buttons is not { IsEditingModes: true } buttons) return;
        if (TabModel(tab) is { } model && buttons.RenamingModeId != model.Id) buttons.StartRename(model.Id);
    }

    /// <summary>A right-click on a tab opens its menu.</summary>
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || Up<RadioButton>(e.Source, "ModeTab") is not { } tab) return;
        if (TabModel(tab) is { } model) _buttons?.OpenMenu(model.Id);
        e.Handled = true;
    }

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (_buttons is not { } buttons) return;
        if (Up<TextBox>(e.Source, "TabNameBox") is { } box)
        {
            // The box sits inside the tab, which would take Enter and Space as a click.
            switch (e.Key)
            {
                case Key.Enter:
                    CommitTabRename(box);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    var id = buttons.RenamingModeId;
                    buttons.CancelRename();
                    if (id is not null) FocusTab(id);
                    e.Handled = true;
                    break;
                case Key.Space:
                    e.Handled = true;   // the space itself arrives as text input
                    break;
            }
            return;
        }
        // The context-menu key, or Shift+F10, on a focused tab opens its menu.
        if (e.Source is RadioButton { Name: "ModeTab" } tab
            && (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            && TabModel(tab) is { } model)
        {
            buttons.OpenMenu(model.Id);
            e.Handled = true;
        }
    }

    private void OnTabKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && Up<TextBox>(e.Source, "TabNameBox") is not null) e.Handled = true;
    }

    /// <summary>Leaving the rename box commits it.</summary>
    private void OnTabNameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TextBox { Name: "TabNameBox" } box || _buttons is not { RenamingModeId: { } id }) return;
        if (TabModel(box)?.Id == id) CommitTabRename(box);
    }

    private void CommitTabRename(TextBox box)
    {
        if (_buttons is not { RenamingModeId: { } id } buttons) return;
        var focused = box.IsKeyboardFocusWithin;
        buttons.RenameText = box.Text ?? "";
        buttons.CommitRename();
        if (focused) FocusTab(id);
    }

    private void FocusTab(string id)
        => Dispatcher.UIThread.Post(() => ModeTabReorder.TabAt(_modeTabs, TabIndexOf(id))?.Focus(NavigationMethod.Tab),
                                    DispatcherPriority.Loaded);

    private void FocusTabNameBox(string id)
    {
        if (_buttons is not { } buttons || buttons.RenamingModeId != id) return;
        _modeTabs.UpdateLayout();
        var box = ModeTabReorder.TabAt(_modeTabs, TabIndexOf(id))?.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(b => b.Name == "TabNameBox");
        if (box is null) return;
        box.Text = buttons.RenameText;
        box.Focus(NavigationMethod.Tab);
        box.SelectAll();
    }
}
