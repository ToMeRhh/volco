using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Hardware;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Pages;

/// <summary>Hardware-page spec §5.2, §5.3, §6.1, §6.2, §6.4 and §6.5: the inspector of the
/// selected tile or bus on the stage in Hardware mode.</summary>
public sealed class HardwareInspectorTests : IDisposable
{
    public HardwareInspectorTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(StagePage Page, HardwareInspectorHost Host, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public HardwareViewModel Hardware => Vm.Hardware;

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The stage in a 1100 × 720 window over the DIY setup (5 sliders; buttons LED, LED,
    /// pair), in sync with the controller so the draft can be edited.</summary>
    private static Shown Show(StageMode mode = StageMode.Hardware)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var host = new HardwareInspectorHost { DataContext = vm.Hardware };
        var page = new StagePage(vm.Controls) { Hardware = vm.Hardware, HardwareSidePanel = host };
        var window = new Window { Content = page, Width = 1100, Height = 720 };
        window.Show();

        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                     ControllerConfigCodec.Crc32(DiyBlob), 5, DiyKinds));
        Until(window, () => vm.Controller.CanEdit);
        page.Mode = mode;
        Settle(window);
        return new Shown(page, host, window, rig, vm);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Window window, Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Settle(window);
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<T> Tiles<T>(Visual root) where T : Control
        => root.GetVisualDescendants().OfType<T>().ToList();

    /// <summary>A left click at <paramref name="y"/> from the control's top (its middle when null).</summary>
    private static void Click(Shown s, Control control, double? y = null)
    {
        control.BringIntoView();
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, y ?? control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    private static string Heading(Shown s) => In<TextBlock>(s.Host, "HeadingText").Text!;

    private static string PinValue(Visual field) => In<TextBlock>(field, "ValueText").Text!;

    /// <summary>Spec §3.2, §4: the device card tops the side panel, over the inspector.</summary>
    [AvaloniaFact]
    public void TheDeviceCardTopsTheSidePanel()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Sliders.Items[0]);
        Settle(s.Window);

        var card = In<Volco.App.Views.Hardware.DeviceCard>(s.Host, "Card");
        Assert.Equal("Volco controller", In<TextBlock>(card, "CardName").Text);
        Assert.StartsWith("Connected over USB", In<TextBlock>(card, "StatusText").Text);
        Assert.Equal("Development firmware", In<TextBlock>(card, "DetailText").Text);
        Assert.False(In<Button>(card, "ActionButton").IsVisible);
        var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), s.Host)!.Value.Y;
        Assert.True(In<TextBlock>(s.Host, "HeadingText").TranslatePoint(new Point(0, 0), s.Host)!.Value.Y >= cardBottom);

        Click(s, In<Button>(card, "StatusButton"));
        Assert.True(s.Hardware.IsConnectionDialogOpen);
    }

    [AvaloniaFact]
    public void SelectingATileShowsItsWiring()
    {
        using var s = Show();
        Click(s, Tiles<SliderTile>(s.Page)[2], y: 6);

        Assert.Equal("Slider 3", Heading(s));
        Assert.Equal("GPIO 32", PinValue(In<PinField>(s.Host, "PinField")));
        Assert.StartsWith("Controls: ", In<TextBlock>(s.Host, "SubtitleText").Text);
    }

    [AvaloniaFact]
    public void SelectionCarriesOverFromControls()
    {
        using var s = Show(StageMode.Controls);
        Click(s, Tiles<ButtonTile>(s.Page)[1], y: 6);
        Assert.Same(s.Vm.Buttons.Items[1], s.Vm.Controls.Selected);

        s.Page.Mode = StageMode.Hardware;
        Settle(s.Window);
        Assert.Equal("Button 2", Heading(s));
        Assert.Equal("GPIO 14", PinValue(In<PinField>(s.Host, "PinField")));
    }

    [AvaloniaFact]
    public void LightSegmentReshapesOnlyTheRowsBelowIt()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        var pin = In<PinField>(s.Host, "PinField");
        var y = pin.TranslatePoint(new Point(0, 0), s.Host)!.Value.Y;
        Assert.True(In<Panel>(s.Host, "LightPinRow").IsVisible);
        Assert.Equal("GPIO 12", PinValue(In<PinField>(s.Host, "LightPinField")));
        Assert.True(In<RadioButton>(s.Host, "LightLed").IsChecked);

        Click(s, In<RadioButton>(s.Host, "LightScreen"));
        Assert.Equal(3, Assert.IsType<ButtonWiringViewModel>(s.Hardware.Inspected).LightIndex);

        Assert.Same(pin, In<PinField>(s.Host, "PinField"));
        Assert.Equal(y, pin.TranslatePoint(new Point(0, 0), s.Host)!.Value.Y, 0.5);
        Assert.True(In<Grid>(s.Host, "BusRow").IsVisible);
        Assert.True(In<Grid>(s.Host, "AddressRow").IsVisible);
        Assert.False(In<Panel>(s.Host, "LightPinRow").IsVisible);
        Assert.False(In<Grid>(s.Host, "LitRow").IsVisible);
        Assert.Equal(Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Screen, s.Vm.Controller.Draft.Buttons[0].IndicatorCase);
    }

    /// <summary>Spec §6: every row is one line of 44 px, its label on the left and its control on
    /// the right, at the same height.</summary>
    [AvaloniaTheory]
    [InlineData("LightLed", 4)]
    [InlineData("LightPair", 5)]
    [InlineData("LightScreen", 4)]
    [InlineData("LightNone", 2)]
    public void ButtonInspectorRowsAreOneLineEach(string light, int rows)
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        Click(s, In<RadioButton>(s.Host, light));

        var shown = s.Host.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.Classes.Contains("InspectorRow") && g.IsEffectivelyVisible).ToList();
        Assert.Equal(rows, shown.Count);
        foreach (var row in shown)
        {
            Assert.Equal(44, row.Bounds.Height, 1.0);
            var label = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("RowLabel"));
            var control = row.Children.Single(c => Grid.GetColumn(c) == 1);
            var labelBox = new Rect(label.TranslatePoint(new Point(0, 0), s.Host)!.Value, label.Bounds.Size);
            var controlBox = new Rect(control.TranslatePoint(new Point(0, 0), s.Host)!.Value, control.Bounds.Size);
            Assert.Equal(labelBox.Center.Y, controlBox.Center.Y, 2.0);
            Assert.True(labelBox.Right <= controlBox.Left, $"{label.Text}: the label is left of its control");
        }
        // Remove stays at the bottom, outside the scrolling rows.
        var remove = In<Button>(s.Host, "RemoveButton");
        Assert.Null(remove.FindAncestorOfType<ScrollViewer>());
    }

    [AvaloniaFact]
    public void BusChipSelectsTheBus()
    {
        using var s = Show();
        Assert.False(In<ItemsControl>(s.Page, "BusChipList").IsVisible);   // no screen yet
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        Click(s, In<RadioButton>(s.Host, "LightScreen"));   // a screen on bus 0

        var chip = Tiles<Button>(s.Page).Single(b => b.Name == "BusChip");
        Assert.Equal("I2C bus 0 · SDA 25 · SCL 26", chip.Content);
        Click(s, chip);

        Assert.Equal("I2C bus 0", Heading(s));
        Assert.Equal("GPIO 25", PinValue(In<PinField>(s.Host, "SdaField")));
        Assert.Equal("GPIO 26", PinValue(In<PinField>(s.Host, "SclField")));
        Assert.Equal(1, In<ComboBox>(s.Host, "ClockBox").SelectedIndex);   // 400 kHz
        Assert.Contains("selected", chip.Classes);
        Assert.Null(s.Vm.Controls.Selected);

        Click(s, Tiles<SliderTile>(s.Page)[0], y: 6);
        Assert.Equal("Slider 1", Heading(s));
        Assert.DoesNotContain("selected", chip.Classes);
    }

    [AvaloniaFact]
    public void RemoveAsksThenRemoves()
    {
        using var s = Show();
        Click(s, Tiles<SliderTile>(s.Page)[2], y: 6);
        Assert.Equal("Remove Slider 3", In<Button>(s.Host, "RemoveButton").Content);

        Click(s, In<Button>(s.Host, "RemoveButton"));
        Assert.Equal("Remove Slider 3?", In<TextBlock>(s.Host, "ConfirmText").Text);
        Assert.True(In<Border>(s.Host, "ConfirmPanel").IsVisible);
        Click(s, In<Button>(s.Host, "CancelConfirmButton"));
        Assert.False(In<Border>(s.Host, "ConfirmPanel").IsVisible);
        Assert.Equal(5, Tiles<SliderTile>(s.Page).Count);

        Click(s, In<Button>(s.Host, "RemoveButton"));
        Assert.Equal("Remove", In<Button>(s.Host, "ConfirmButton").Content);
        Click(s, In<Button>(s.Host, "ConfirmButton"));
        Assert.Equal(4, Tiles<SliderTile>(s.Page).Count);
        Assert.Equal(4, s.Vm.Controller.Draft.Sliders.Count);
        // The slider that was Slider 4 is now Slider 3, and selected.
        Assert.Equal("Slider 3", Heading(s));
        Assert.Equal("GPIO 36", PinValue(In<PinField>(s.Host, "PinField")));

        // Controls still draws Volco's five sliders until the save.
        s.Page.Mode = StageMode.Controls;
        Settle(s.Window);
        Assert.Equal(5, Tiles<SliderTile>(s.Page).Count);
    }
}
