using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Controls;

/// <summary>Hardware-page spec §6.3: the pin field control.</summary>
public class PinFieldTests
{
    private sealed record Shown(Window Window, PinField Field, PinFieldViewModel Vm, List<uint?> Sets);

    private static Shown Show(uint? value = 4)
    {
        var draft = new ControllerConfig();
        var sets = new List<uint?>();
        uint? current = value;
        var vm = new PinFieldViewModel("Button pin", PinRole.ButtonInput, new PinOwner(PinOwnerKind.Button, 0),
                                       () => draft, () => current, v => { sets.Add(v); current = v; }, null);
        var field = new PinField { DataContext = vm, Width = 240, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Content = field, Width = 600, Height = 500 };
        window.Show();
        Settle(window);
        return new Shown(window, field, vm, sets);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Open(Shown s)
    {
        Named<Button>(s.Field, "ValueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Assert.False(Named<TextBox>(s.Field, "QueryBox").IsVisible);
    }

    [AvaloniaFact]
    public void OpenShowsTheListAndFocusesTheBox()
    {
        var s = Show();
        Open(s);
        var popup = Named<Popup>(s.Field, "ListPopup");
        Assert.True(popup.IsOpen);
        var box = Named<TextBox>(s.Field, "QueryBox");
        Assert.True(box.IsVisible);
        Assert.True(box.IsFocused);
        var rows = popup.Child!.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "PinRowButton").ToList();
        Assert.Equal(s.Vm.Rows.Count, rows.Count);
        Assert.False(rows.Single(b => ((PinRowViewModel)b.DataContext!).Pin == 7).IsEnabled);   // flash: greyed

        Press(s, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
        Press(s, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.Equal(new uint?[] { 5 }, s.Sets);
        Assert.False(popup.IsOpen);
        Assert.Equal("GPIO 5", Named<TextBlock>(s.Field, "ValueText").Text);

        Open(s);
        Assert.True(popup.IsOpen);
        Press(s, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
        Press(s, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(popup.IsOpen);
        Assert.Equal(new uint?[] { 5 }, s.Sets);
    }

    [AvaloniaFact]
    public void ClickingARowPicksIt()
    {
        var s = Show();
        Open(s);
        var row = Named<Popup>(s.Field, "ListPopup").Child!.GetVisualDescendants().OfType<Button>()
            .First(b => b.Name == "PinRowButton" && ((PinRowViewModel)b.DataContext!).Pin == 13);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
        Assert.Equal(new uint?[] { 13 }, s.Sets);
        Assert.False(s.Vm.IsOpen);
    }

    [AvaloniaFact]
    public void AClickOutsideClosesWithoutChange()
    {
        var s = Show();
        Open(s);
        s.Window.MouseDown(new Point(590, 490), MouseButton.Left);
        s.Window.MouseUp(new Point(590, 490), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.IsOpen);
        Assert.Empty(s.Sets);
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
