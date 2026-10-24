using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Volco.App.Controls;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Hardware;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Controls;

/// <summary>Hardware-page spec §6.3: the pin field control and its pin dialog.</summary>
public class PinFieldTests
{
    private sealed record Shown(Window Window, PinField Field, PinDialog Dialog, PinFieldViewModel Vm, List<uint?> Sets, FakeTimeProvider Time);

    /// <summary>The field and, beside it, the dialog over the same view model (the settings window
    /// hosts the dialog; here it is shown directly).</summary>
    private static Shown Show(uint? value = 4, Func<Volco.Core.Communication.PinWatchRole, IDisposable?>? watch = null)
    {
        var draft = new ControllerConfig();
        var sets = new List<uint?>();
        uint? current = value;
        var time = new FakeTimeProvider();
        var vm = new PinFieldViewModel("Button pin", PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 0),
                                       () => draft, () => current, v => { sets.Add(v); current = v; }, null, watch, time);
        var field = new PinField { DataContext = vm, Width = 240, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var dialog = new PinDialog { DataContext = vm, Width = 760 };
        var window = new Window { Content = new StackPanel { Children = { field, dialog } }, Width = 800, Height = 700 };
        window.Show();
        Settle(window);
        return new Shown(window, field, dialog, vm, sets, time);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Button Tile(Shown s, uint pin)
        => s.Dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "PinTile" && ((PinRowViewModel)b.DataContext!).Pin == pin);

    private static void Open(Shown s)
    {
        Named<Button>(s.Field, "ValueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Named<Border>(s.Dialog, "PinGrid").Focus();
        Settle(s.Window);
    }

    private static void Press(Shown s, Key key, PhysicalKey physical)
    {
        s.Window.KeyPress(key, RawInputModifiers.None, physical, null);
        Settle(s.Window);
    }

    [AvaloniaFact]
    public void ClosedShowsTheValue()
    {
        var s = Show();
        Assert.Equal("GPIO 4", Named<TextBlock>(s.Field, "ValueText").Text);
        Assert.Equal("Button pin", Named<TextBlock>(s.Field, "LabelText").Text);
        Assert.False(s.Vm.IsOpen);
    }

    [AvaloniaFact]
    public void TheDialogShowsEveryPinAtOnce_EightToARow()
    {
        var s = Show();
        Open(s);
        Assert.True(s.Vm.IsOpen);
        Assert.Equal("Button 1 pin", Named<TextBlock>(s.Dialog, "PinDialogTitle").Text);
        var tiles = s.Dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "PinTile").ToList();
        Assert.Equal(s.Vm.Rows.Count, tiles.Count);
        Assert.All(tiles, t => Assert.True(t.IsEffectivelyVisible));
        Assert.False(Tile(s, 7).IsEnabled);                  // flash: greyed
        Assert.Contains("current", Tile(s, 4).Classes);
        var origin = Tile(s, 0).TranslatePoint(new Point(0, 0), s.Dialog)!.Value;
        Assert.Equal(origin.Y, Tile(s, 7).TranslatePoint(new Point(0, 0), s.Dialog)!.Value.Y, 1);   // 0-7 share a row
        Assert.Equal(origin.X, Tile(s, 8).TranslatePoint(new Point(0, 0), s.Dialog)!.Value.X, 1);   // 8 starts the next
        Assert.True(Tile(s, 8).TranslatePoint(new Point(0, 0), s.Dialog)!.Value.Y > origin.Y);
        Assert.Equal(origin.X, Tile(s, 32).TranslatePoint(new Point(0, 0), s.Dialog)!.Value.X, 1);  // gaps keep the columns
    }

    [AvaloniaFact]
    public void ArrowsMoveAndEnterPicks_EscapeKeepsTheValue()
    {
        var s = Show();
        Open(s);
        Press(s, Key.Right, PhysicalKey.ArrowRight);
        Press(s, Key.Down, PhysicalKey.ArrowDown);
        Assert.Contains("highlighted", Tile(s, 13).Classes);
        Press(s, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
        Assert.False(s.Vm.IsOpen);
        Assert.Equal("GPIO 13", Named<TextBlock>(s.Field, "ValueText").Text);

        Open(s);
        Press(s, Key.Right, PhysicalKey.ArrowRight);
        Press(s, Key.Escape, PhysicalKey.Escape);
        Assert.False(s.Vm.IsOpen);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
    }

    [AvaloniaFact]
    public void ClickingAPinPicksIt_CancelDoesNot()
    {
        var s = Show();
        Open(s);
        Tile(s, 13).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
        Assert.False(s.Vm.IsOpen);

        Open(s);
        Named<Button>(s.Dialog, "PinCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.False(s.Vm.IsOpen);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
    }

    [AvaloniaFact]
    public void Activity_MarksThePinByColour_NowThenBefore()
    {
        var s = Show(watch: _ => new Noop());
        Open(s);
        Assert.True(Named<TextBlock>(s.Dialog, "WatchHintText").IsVisible);
        s.Vm.OnPinActivity(15);
        Settle(s.Window);
        var tile = Tile(s, 15);
        Assert.Contains("now", tile.Classes);
        Assert.DoesNotContain("highlighted", tile.Classes);   // marked, not selected
        Assert.False(Named<Ellipse>(tile, "MovedDot").IsVisible);
        Assert.True(Named<Ellipse>(tile, "MovingDot").IsVisible);        // the green dot
        Assert.Equal("free", Named<TextBlock>(tile, "PinNote").Text);   // no words for the state

        s.Time.Advance(PinFieldViewModel.ActivityHold);
        Settle(s.Window);
        Assert.DoesNotContain("now", tile.Classes);
        Assert.Contains("before", tile.Classes);
        Assert.True(Named<Ellipse>(tile, "MovedDot").IsVisible);
        Assert.False(Named<Ellipse>(tile, "MovingDot").IsVisible);
        Assert.Equal("free", Named<TextBlock>(tile, "PinNote").Text);
    }

    /// <summary>Opened by + Add: no pins until one is heard, Add enabled once one is selected,
    /// "Choose manually" for the grid, Cancel and Add closing.</summary>
    [AvaloniaFact]
    public void Adding_ListsOnlyTheHeardPins_ThenAdds()
    {
        var s = Show(value: null, watch: _ => new Noop());
        var cancels = 0;
        s.Vm.OpenToAdd(() => cancels++);
        Settle(s.Window);
        Assert.Equal("Add Button 1", Named<TextBlock>(s.Dialog, "PinDialogTitle").Text);
        Assert.Equal("Listening for button presses…", Named<TextBlock>(s.Dialog, "WatchHintText").Text);
        Assert.True(Named<Ellipse>(s.Dialog, "ListeningDot").IsEffectivelyVisible);
        Assert.False(Named<TextBlock>(s.Dialog, "WatchDetailText").IsEffectivelyVisible);
        Assert.False(Named<ItemsControl>(s.Dialog, "PinCells").IsEffectivelyVisible);
        Assert.True(Named<Ellipse>(s.Dialog, "WaitingDot").IsEffectivelyVisible);
        Assert.DoesNotContain(s.Dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "PinTile" && b.IsEffectivelyVisible);
        var add = Named<Button>(s.Dialog, "PinAddButton");
        Assert.True(add.IsEffectivelyVisible);
        Assert.Equal("Add button", add.Content);
        Assert.False(add.IsEffectivelyEnabled);

        s.Vm.OnPinActivity(15);
        Settle(s.Window);
        var shown = s.Dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "PinTile" && b.IsEffectivelyVisible).ToList();
        var tile = Assert.Single(shown);
        Assert.Equal(15u, ((PinRowViewModel)tile.DataContext!).Pin);
        Assert.Contains("highlighted", tile.Classes);   // the first heard is selected
        Assert.False(Named<Ellipse>(s.Dialog, "WaitingDot").IsEffectivelyVisible);
        Assert.True(add.IsEffectivelyEnabled);

        Named<Button>(s.Dialog, "ChooseManuallyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.True(Named<ItemsControl>(s.Dialog, "PinCells").IsEffectivelyVisible);
        Assert.True(Named<TextBlock>(s.Dialog, "WatchDetailText").IsEffectivelyVisible);
        Assert.False(Named<Button>(s.Dialog, "ChooseManuallyButton").IsEffectivelyVisible);
        Tile(s, 13).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));   // selects, doesn't close
        Settle(s.Window);
        Assert.True(s.Vm.IsOpen);
        Assert.Empty(s.Sets);

        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.False(s.Vm.IsOpen);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
        Assert.Equal(0, cancels);
        Assert.False(Named<Button>(s.Dialog, "PinAddButton").IsVisible);   // opened from the field: no Add
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }

    [AvaloniaFact]
    public void AProblemMarksTheFieldWithItsFix()
    {
        var s = Show();
        s.Vm.SetProblem("Slider 1 also uses GPIO 33.", new PinOwner(PinOwnerKind.Slider, 0));
        Settle(s.Window);
        Assert.Contains("invalid", s.Field.Classes);
        Assert.Equal("Slider 1 also uses GPIO 33.", Named<TextBlock>(s.Field, "ErrorText").Text);
        Assert.True(Named<TextBlock>(s.Field, "ErrorText").IsVisible);
        // Without a swap callback there is no fix to offer.
        Assert.False(Named<Button>(s.Field, "FixButton").IsVisible);

        s.Vm.SetProblem(null, null);
        Settle(s.Window);
        Assert.DoesNotContain("invalid", s.Field.Classes);
        Assert.False(Named<TextBlock>(s.Field, "ErrorText").IsVisible);
    }

    [AvaloniaFact]
    public void DisabledWhileTheDraftCantBeEdited()
    {
        var s = Show();
        s.Vm.IsEditable = false;
        Settle(s.Window);
        Assert.False(Named<Button>(s.Field, "ValueButton").IsEnabled);
    }
}
