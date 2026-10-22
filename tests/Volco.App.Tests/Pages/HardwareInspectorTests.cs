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

/// <summary>Hardware-page spec §5.2, §5.3, §6.1, §6.2, §6.4 and §6.5: the wiring of the selected
/// tile (its inspector's Hardware section, opened here) or bus in the Controller page's side panel,
/// under the device card.</summary>
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
    private static Shown Show()
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

    /// <summary>The heading of what the side panel shows: the tile's inspector or the bus's.</summary>
    private static string Heading(Shown s)
        => s.Page.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "HeadingText" && t.IsEffectivelyVisible).Text!;

    private static Volco.App.Views.Inspector.HardwareSection Section(Shown s)
        => s.Page.GetVisualDescendants().OfType<Volco.App.Views.Inspector.HardwareSection>().Single();

    private static string PinValue(Visual field) => In<TextBlock>(field, "ValueText").Text!;

    /// <summary>Controller-page spec §4.3, §5: the status is in the sidebar and the banner, not
    /// the side panel; the tile's inspector is under the controller's panel.</summary>
    [AvaloniaFact]
    public void TheTileInspectorIsUnderTheControllersPanel()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Sliders.Items[0]);
        Settle(s.Window);

        Assert.Empty(s.Page.GetVisualDescendants().OfType<Volco.App.Views.Hardware.SidebarStatus>());
        Assert.Empty(s.Page.GetVisualDescendants().OfType<Volco.App.Views.Hardware.StatusBanner>());
        // The tile's inspector, with its Hardware section, is under the controller's panel.
        var controls = In<TextBlock>(In<ContentControl>(s.Page, "InspectorContent"), "HeadingText");
        Assert.Equal("Slider 1", controls.Text);
        Assert.True(controls.TranslatePoint(new Point(0, 0), s.Page)!.Value.Y
                    >= s.Host.TranslatePoint(new Point(0, s.Host.Bounds.Height), s.Page)!.Value.Y);
    }

    [AvaloniaFact]
    public void SelectingATileShowsItsWiring()
    {
        using var s = Show();
        Click(s, Tiles<SliderTile>(s.Page)[2], y: 6);

        Assert.Equal("Slider 3", Heading(s));
        Assert.Equal("GPIO 32", PinValue(In<PinField>(s.Page, "PinField")));
        Assert.Contains(Section(s).GetVisualDescendants(), v => v is PinField { Name: "PinField" });
    }

    [AvaloniaFact]
    public void LightSegmentReshapesOnlyTheRowsBelowIt()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        var pin = In<PinField>(s.Page, "PinField");
        var y = pin.TranslatePoint(new Point(0, 0), s.Page)!.Value.Y;
        Assert.True(In<Panel>(s.Page, "LightPinRow").IsVisible);
        Assert.Equal("GPIO 12", PinValue(In<PinField>(s.Page, "LightPinField")));
        Assert.True(In<RadioButton>(s.Page, "LightLed").IsChecked);

        Click(s, In<RadioButton>(s.Page, "LightScreen"));
        Assert.Equal(3, Assert.IsType<ButtonWiringViewModel>(s.Hardware.Inspected).LightIndex);

        Assert.Same(pin, In<PinField>(s.Page, "PinField"));
        Assert.Equal(y, pin.TranslatePoint(new Point(0, 0), s.Page)!.Value.Y, 0.5);
        Assert.True(In<Grid>(s.Page, "BusRow").IsVisible);
        Assert.True(In<Grid>(s.Page, "AddressRow").IsVisible);
        Assert.False(In<Panel>(s.Page, "LightPinRow").IsVisible);
        Assert.False(In<Grid>(s.Page, "LitRow").IsVisible);
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
        Click(s, In<RadioButton>(s.Page, light));

        var section = Section(s);
        var shown = section.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.Classes.Contains("InspectorRow") && g.IsEffectivelyVisible).ToList();
        Assert.Equal(rows, shown.Count);
        foreach (var row in shown)
        {
            Assert.Equal(44, row.Bounds.Height, 1.0);
            var label = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("RowLabel"));
            var control = row.Children.Single(c => Grid.GetColumn(c) == 1);
            var labelBox = new Rect(label.TranslatePoint(new Point(0, 0), section)!.Value, label.Bounds.Size);
            var controlBox = new Rect(control.TranslatePoint(new Point(0, 0), section)!.Value, control.Bounds.Size);
            Assert.Equal(labelBox.Center.Y, controlBox.Center.Y, 2.0);
            Assert.True(labelBox.Right <= controlBox.Left, $"{label.Text}: the label is left of its control");
        }
        // Remove is the section's last row (controller-page spec §4.3), under the wiring rows.
        var remove = In<Button>(section, "HardwareRemoveButton");
        Assert.Equal("Remove Button 1", remove.Content);
        Assert.All(shown, row => Assert.True(row.TranslatePoint(new Point(0, 0), section)!.Value.Y
                                             < remove.TranslatePoint(new Point(0, 0), section)!.Value.Y));
    }

    /// <summary>A screen button's bus link opens the bus's row in Controller settings (controller-page
    /// spec §4.3): its pins and clock are fields of the row.</summary>
    [AvaloniaFact]
    public void TheBusLinkOpensTheBusRow()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        Click(s, In<RadioButton>(s.Page, "LightScreen"));   // a screen on bus 0

        Assert.Equal("Bus 0, address 3C", In<TextBlock>(s.Page, "BusLinkText").Text);
        Click(s, In<Button>(s.Page, "BusLink"));

        Assert.Equal("Controller", Heading(s));
        Assert.Equal("I2C bus 0 · SDA 25 · SCL 26 · 400 kHz", In<TextBlock>(s.Page, "BusSummary").Text);
        Assert.Equal("GPIO 25", PinValue(In<PinField>(s.Page, "SdaField")));
        Assert.Equal("GPIO 26", PinValue(In<PinField>(s.Page, "SclField")));
        Assert.Equal(1, In<ComboBox>(s.Page, "ClockBox").SelectedIndex);   // 400 kHz
        Assert.Null(s.Vm.Controls.Selected);

        Click(s, Tiles<SliderTile>(s.Page)[0], y: 6);
        Assert.Equal("Slider 1", Heading(s));
    }

    [AvaloniaFact]
    public void RemoveAsksThenRemoves()
    {
        using var s = Show();
        Click(s, Tiles<SliderTile>(s.Page)[2], y: 6);
        Assert.Equal("Remove Slider 3", In<Button>(s.Page, "HardwareRemoveButton").Content);

        Click(s, In<Button>(s.Page, "HardwareRemoveButton"));
        Assert.Equal("Remove Slider 3?", In<TextBlock>(s.Page, "ConfirmText").Text);
        Assert.True(In<Border>(s.Page, "ConfirmPanel").IsVisible);
        Click(s, In<Button>(s.Page, "CancelConfirmButton"));
        Assert.False(In<Border>(s.Page, "ConfirmPanel").IsVisible);
        Assert.Equal(5, Tiles<SliderTile>(s.Page).Count);

        Click(s, In<Button>(s.Page, "HardwareRemoveButton"));
        Assert.Equal("Remove", In<Button>(s.Page, "ConfirmButton").Content);
        Click(s, In<Button>(s.Page, "ConfirmButton"));
        Assert.Equal(4, Tiles<SliderTile>(s.Page).Count);
        Assert.Equal(4, s.Vm.Controller.Draft.Sliders.Count);
        // The slider that was Slider 4 is now Slider 3, and selected: the header follows the draft.
        Assert.Same(s.Vm.Sliders.Items[3], s.Vm.Controls.Selected);
        Assert.Equal("Slider 3", Heading(s));
        Assert.Equal("Remove Slider 3", In<Button>(s.Page, "HardwareRemoveButton").Content);
        Assert.Equal("GPIO 36", PinValue(In<PinField>(s.Page, "PinField")));

        // Volco's settings keep five sliders until the save.
        Assert.Equal(5, s.Vm.Sliders.Items.Count);
    }
}
