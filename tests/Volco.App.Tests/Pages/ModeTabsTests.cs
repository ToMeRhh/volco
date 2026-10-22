using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Config;
using Volco.Core.Input;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §9: the mode tab strip, its menu, Add mode, Make
/// live and edit mode (rename, delete, reorder).</summary>
public class ModeTabsTests
{
    private sealed record Shown(StagePage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ButtonsViewModel Buttons => Vm.Buttons;

        public void Dispose()
        {
            if (Page.ModeMenu.IsOpen) Page.ModeMenu.Hide();
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The page with modes Default, then <paramref name="extra"/> (ids are the names in
    /// lower case).</summary>
    private static Shown Show(params string[] extra)
    {
        var rig = new TestRig();
        foreach (var name in extra)
            rig.Config.Config.Modes.Add(new ModeConfig { Id = name.ToLowerInvariant(), Name = name });
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<RadioButton> Tabs(Shown s)
        => s.Page.GetVisualDescendants().OfType<RadioButton>().Where(t => t.Name == "ModeTab").ToList();

    private static List<string> TabNames(Shown s) => s.Buttons.Modes.Select(p => p.Name).ToList();

    /// <summary>A point inside <paramref name="control"/> in its top level's coordinates; by
    /// default its centre.</summary>
    private static Point At(Control control, double? x = null)
        => control.TranslatePoint(new Point(x ?? control.Bounds.Width / 2, control.Bounds.Height / 2),
                                  TopLevel.GetTopLevel(control)!)!.Value;

    private static void Click(Shown s, Control control, MouseButton button = MouseButton.Left, double? x = null)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var point = At(control, x);
        top.MouseDown(point, button);
        top.MouseUp(point, button);
        Settle(s.Window);
    }

    /// <summary>A click on a tab's name, clear of its ×.</summary>
    private static void ClickTab(Shown s, RadioButton tab, MouseButton button = MouseButton.Left)
        => Click(s, tab, button, x: 14);

    private static void Press(Shown s, TopLevel top, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        top.KeyPress(key, modifiers, physical, null);
        top.KeyRelease(key, modifiers, physical, null);
        Settle(s.Window);
    }

    private static void Drag(Shown s, RadioButton from, RadioButton to)
    {
        var start = At(from, x: 14);
        var end = At(to);
        s.Window.MouseDown(start, MouseButton.Left);
        s.Window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.LeftMouseButton);
        s.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        s.Window.MouseUp(end, MouseButton.Left);
        Settle(s.Window);
    }

    private static List<string> CopyFromItems(Shown s)
        => ((MenuFlyout)s.Page.MenuCopyFrom.Flyout!).ItemsSource!.Cast<MenuItem>().Select(i => (string)i.Header!).ToList();

    // ── The strip ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Strip_RightEnd_Order_MakeLive_AddMode_Pencil()
    {
        using var s = Show("Gaming");
        var makeLive = s.Page.FindControl<Button>("MakeLiveButton")!;
        Assert.False(makeLive.IsVisible);   // the edited mode is live

        ClickTab(s, Tabs(s)[1]);
        Assert.Equal("gaming", s.Buttons.EditingModeId);
        Assert.True(makeLive.IsVisible);
        Assert.Equal("Make live", makeLive.Content);
        Assert.Equal("Make Gaming live", ToolTip.GetTip(makeLive));

        var addMode = s.Page.FindControl<Button>("AddModeButton")!;
        var pencil = s.Page.FindControl<Button>("EditModesButton")!;
        Assert.Equal("Add an empty mode", ToolTip.GetTip(addMode));
        Assert.Contains(addMode.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Add mode");
        var xs = new[] { makeLive, addMode, pencil }.Select(b => b.TranslatePoint(default, s.Window)!.Value.X).ToList();
        Assert.True(xs[0] < xs[1] && xs[1] < xs[2], string.Join(", ", xs));
        Assert.True(xs[0] > Tabs(s)[1].TranslatePoint(default, s.Window)!.Value.X);
    }

    [AvaloniaFact]
    public void Pencil_TogglesEditMode_IconAndTooltip()
    {
        using var s = Show("Gaming");
        var pencil = s.Page.FindControl<Button>("EditModesButton")!;
        Assert.Equal("Edit modes", ToolTip.GetTip(pencil));
        Assert.True(In<PathIcon>(pencil, "PencilIcon").IsVisible);
        Assert.False(In<PathIcon>(pencil, "CheckIcon").IsVisible);

        Click(s, pencil);
        Assert.True(s.Buttons.IsEditingModes);
        Assert.Equal("Done", ToolTip.GetTip(pencil));
        Assert.False(In<PathIcon>(pencil, "PencilIcon").IsVisible);
        Assert.True(In<PathIcon>(pencil, "CheckIcon").IsVisible);
        Assert.True(Application.Current!.TryFindResource("Icon.Check", out var check));
        Assert.Same(check, In<PathIcon>(pencil, "CheckIcon").Data);

        Click(s, pencil);
        Assert.False(s.Buttons.IsEditingModes);
        Assert.Equal("Edit modes", ToolTip.GetTip(pencil));
    }

    // ── The menu ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AddMode_OpensMenu_NameFocused_EnterSavesName()
    {
        using var s = Show();
        Click(s, s.Page.FindControl<Button>("AddModeButton")!);

        Assert.Equal(2, Tabs(s).Count);
        Assert.True(s.Page.ModeMenu.IsOpen);
        var box = s.Page.MenuNameBox;
        Assert.True(box.IsFocused);
        Assert.Equal("Mode 2", box.Text);
        Assert.Equal("Mode 2", box.SelectedText);
        Assert.True(s.Page.MenuMakeLive.IsVisible);   // the new mode isn't live
        Assert.Equal(new[] { "Default" }, CopyFromItems(s));

        var popup = TopLevel.GetTopLevel(box)!;
        popup.KeyTextInput("Gaming");
        Press(s, popup, Key.Enter, PhysicalKey.Enter);

        Assert.False(s.Page.ModeMenu.IsOpen);
        Assert.Null(s.Buttons.MenuModeId);
        Assert.Equal(new[] { "Default", "Gaming" }, TabNames(s));
        Assert.Equal("Gaming", s.Buttons.EditingName);
    }

    [AvaloniaFact]
    public void RightClickTab_OpensMenu_CopyFrom_ListsOtherModes()
    {
        using var s = Show("Gaming", "Work");
        ClickTab(s, Tabs(s)[1], MouseButton.Right);

        Assert.Equal("gaming", s.Buttons.MenuModeId);
        Assert.True(s.Page.ModeMenu.IsOpen);
        Assert.Equal("Gaming", s.Page.MenuNameBox.Text);
        Assert.True(s.Page.MenuMakeLive.IsVisible);
        Assert.Contains(s.Page.MenuCopyFrom.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Copy actions from");
        Assert.Equal(new[] { "Default", "Work" }, CopyFromItems(s));
        Assert.Equal("default", s.Buttons.EditingModeId);   // a right-click doesn't edit the mode

        // Copy actions from replaces the mode's actions (with Undo) and closes the menu.
        s.Rig.Config.Config.Buttons[0].Default().SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        var item = ((MenuFlyout)s.Page.MenuCopyFrom.Flyout!).ItemsSource!.Cast<MenuItem>().First();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Settle(s.Window);
        Assert.False(s.Page.ModeMenu.IsOpen);
        Assert.Null(s.Buttons.MenuModeId);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, s.Rig.Config.Config.Buttons[0].Actions["gaming"].SinglePress.Action);
        Assert.True(s.Vm.Toast.IsOpen);

        // The live mode's menu has no Make live.
        s.Buttons.OpenMenu("default");
        Settle(s.Window);
        Assert.True(s.Page.ModeMenu.IsOpen);
        Assert.False(s.Page.MenuMakeLive.IsVisible);
        Assert.Equal(new[] { "Gaming", "Work" }, CopyFromItems(s));
    }

    [AvaloniaFact]
    public void ContextMenuKey_OnFocusedTab_OpensMenu()
    {
        using var s = Show("Gaming");
        Tabs(s)[1].Focus(NavigationMethod.Tab);
        Press(s, s.Window, Key.Apps, PhysicalKey.ContextMenu);

        Assert.Equal("gaming", s.Buttons.MenuModeId);
        Assert.True(s.Page.ModeMenu.IsOpen);
    }

    [AvaloniaFact]
    public void MenuMakeLive_MakesThatModeLive()
    {
        using var s = Show("Gaming");
        ClickTab(s, Tabs(s)[1], MouseButton.Right);
        Assert.True(s.Page.ModeMenu.IsOpen);

        s.Page.MenuMakeLive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);

        Assert.Equal("gaming", s.Rig.Runtime.ActiveModeId);
        Assert.Equal("gaming", s.Buttons.LiveModeId);
        Assert.Equal("default", s.Buttons.EditingModeId);
        Assert.False(s.Page.ModeMenu.IsOpen);
        Assert.Null(s.Buttons.MenuModeId);
    }

    [AvaloniaFact]
    public void Menu_DoesNotOpen_WhileADialogIsOpen()
    {
        // The action dialog edits a draft of the edited mode; Copy actions from would replace it.
        using var s = Show("Gaming");
        s.Vm.Controls.OpenActionDialog(s.Buttons.Items[0], PressType.Single);
        Settle(s.Window);

        s.Buttons.OpenMenu("default");
        Settle(s.Window);

        Assert.False(s.Page.ModeMenu.IsOpen);
        Assert.Null(s.Buttons.MenuModeId);
        Assert.True(s.Vm.Controls.HasDialog);
    }

    // ── Edit mode ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Editing_ClickTab_RenamesInline_EscapeCancels()
    {
        using var s = Show("Gaming");
        Click(s, s.Page.FindControl<Button>("EditModesButton")!);

        ClickTab(s, Tabs(s)[1]);
        Assert.Equal("gaming", s.Buttons.EditingModeId);
        Assert.Equal("gaming", s.Buttons.RenamingModeId);
        var box = In<TextBox>(Tabs(s)[1], "TabNameBox");
        Assert.True(box.IsEffectivelyVisible);
        Assert.False(In<TextBlock>(Tabs(s)[1], "TabName").IsVisible);
        Assert.True(box.IsFocused);
        Assert.Equal("Gaming", box.SelectedText);

        s.Window.KeyTextInput("Play");
        Press(s, s.Window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(s.Buttons.RenamingModeId);
        Assert.Equal(new[] { "Default", "Gaming" }, TabNames(s));
        Assert.True(Tabs(s)[1].IsFocused);

        // Enter commits.
        ClickTab(s, Tabs(s)[1]);
        Assert.Equal("gaming", s.Buttons.RenamingModeId);
        s.Window.KeyTextInput("Play");
        Press(s, s.Window, Key.Enter, PhysicalKey.Enter);
        Assert.Null(s.Buttons.RenamingModeId);
        Assert.Equal(new[] { "Default", "Play" }, TabNames(s));
    }

    [AvaloniaFact]
    public void Editing_FirstTab_HasStartLabel_NoDelete()
    {
        using var s = Show("Gaming");
        Assert.All(Tabs(s), t => Assert.False(In<TextBlock>(t, "StartLabel").IsVisible));
        Assert.All(Tabs(s), t => Assert.False(In<Button>(t, "DeleteTabButton").IsVisible));

        Click(s, s.Page.FindControl<Button>("EditModesButton")!);
        var tabs = Tabs(s);
        Assert.True(In<TextBlock>(tabs[0], "StartLabel").IsVisible);
        Assert.Equal("start", In<TextBlock>(tabs[0], "StartLabel").Text);
        Assert.False(In<Button>(tabs[0], "DeleteTabButton").IsVisible);
        Assert.False(s.Buttons.Modes[0].DeleteCommand.CanExecute(null));
        Assert.False(In<TextBlock>(tabs[1], "StartLabel").IsVisible);
        Assert.True(In<Button>(tabs[1], "DeleteTabButton").IsVisible);
        Assert.Equal("Delete mode", ToolTip.GetTip(In<Button>(tabs[1], "DeleteTabButton")));
    }

    [AvaloniaFact]
    public void Editing_DeleteTab_UndoRestores()
    {
        using var s = Show("Gaming", "Work");
        Click(s, s.Page.FindControl<Button>("EditModesButton")!);

        Click(s, In<Button>(Tabs(s)[1], "DeleteTabButton"));
        Assert.Equal(new[] { "Default", "Work" }, TabNames(s));
        Assert.True(s.Vm.Toast.IsOpen);
        Assert.Null(s.Buttons.RenamingModeId);   // the × isn't a click on the tab

        s.Vm.Toast.UndoCommand.Execute(null);
        Settle(s.Window);
        Assert.Equal(new[] { "Default", "Gaming", "Work" }, TabNames(s));
    }

    [AvaloniaFact]
    public void Editing_DragTabToFirst_ReordersAndKeepsLive()
    {
        using var s = Show("Gaming", "Work");
        s.Buttons.MakeLive("gaming");
        Settle(s.Window);
        Click(s, s.Page.FindControl<Button>("EditModesButton")!);

        Drag(s, Tabs(s)[2], Tabs(s)[0]);

        Assert.Equal(new[] { "Work", "Default", "Gaming" }, TabNames(s));
        Assert.Equal(new[] { "work", "default", "gaming" }, s.Rig.Config.Config.Modes.Select(p => p.Id));
        Assert.Equal("gaming", s.Buttons.LiveModeId);
        Assert.Equal("default", s.Buttons.EditingModeId);   // a drop isn't a click
        Assert.Null(s.Buttons.RenamingModeId);
        Assert.True(In<TextBlock>(Tabs(s)[0], "StartLabel").IsVisible);   // the new start-up mode
        Assert.All(Tabs(s), t => Assert.Null(t.GetVisualParent()!.RenderTransform));
    }

    [AvaloniaFact]
    public void Editing_AltRight_MovesTab_KeepsFocus()
    {
        using var s = Show("Gaming", "Work");
        Click(s, s.Page.FindControl<Button>("EditModesButton")!);
        Tabs(s)[0].Focus(NavigationMethod.Tab);
        Settle(s.Window);

        Press(s, s.Window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Alt);

        Assert.Equal(new[] { "Gaming", "Default", "Work" }, TabNames(s));
        var focused = s.Window.FocusManager!.GetFocusedElement() as RadioButton;
        Assert.NotNull(focused);
        Assert.Equal("default", (focused!.DataContext as ModeTabViewModel)?.Id);

        Press(s, s.Window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Assert.Equal(new[] { "Default", "Gaming", "Work" }, TabNames(s));
    }

    [AvaloniaFact]
    public void Editing_MoveWithoutHeldButton_DoesNotStartDrag()
    {
        using var s = Show("Gaming", "Work");
        Click(s, s.Page.FindControl<Button>("EditModesButton")!);
        var start = At(Tabs(s)[2], x: 14);
        var end = At(Tabs(s)[0]);

        s.Window.MouseDown(start, MouseButton.Left);
        // The button went up unseen: moves past the threshold with no button held.
        s.Window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.None);
        s.Window.MouseMove(end, RawInputModifiers.None);
        Settle(s.Window);

        Assert.False(s.Page.TabReorder.IsDragging);
        Assert.All(Tabs(s), t => Assert.Null(t.GetVisualParent()!.RenderTransform));
        Assert.All(Tabs(s), t => Assert.DoesNotContain("dragging", t.GetVisualParent()!.Classes));

        s.Window.MouseUp(end, MouseButton.Left);
        Settle(s.Window);
        Assert.Equal(new[] { "Default", "Gaming", "Work" }, TabNames(s));
    }

    [AvaloniaFact]
    public void NotEditing_DragTab_DoesNothing()
    {
        using var s = Show("Gaming", "Work");
        Drag(s, Tabs(s)[2], Tabs(s)[0]);

        Assert.Equal(new[] { "Default", "Gaming", "Work" }, TabNames(s));
        Assert.All(Tabs(s), t => Assert.Null(t.GetVisualParent()!.RenderTransform));
    }
}
