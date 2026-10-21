using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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

/// <summary>Controller-page spec §4.1: the tiles draw the Controls layer, mark wiring problems
/// and new controls, show no pins and no bus chips, and show the raw values during a test.</summary>
public sealed class ControllerTilesTests : IDisposable
{
    public ControllerTilesTests() => StageTransitions.Enabled = false;

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

    /// <summary>The Controller page's stage over the DIY setup (5 sliders, slider 1 on GPIO 33;
    /// buttons LED, LED, pair), in sync with the controller so the draft can be edited.</summary>
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
        Until(window, () => vm.Controller.CanEdit && vm.Hardware.CanStartTest);
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

    private static List<Control> AllTiles(Visual root)
        => root.GetVisualDescendants().Where(v => v is SliderTile or ButtonTile).Cast<Control>().ToList();

    /// <summary>The opacity it is drawn with: its own times its ancestors'.</summary>
    private static double DrawnOpacity(Visual visual)
    {
        var opacity = 1.0;
        for (var v = visual; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        return opacity;
    }

    private static void Click(Shown s, Control control)
    {
        control.BringIntoView();
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    /// <summary>Both ends of a pin conflict get the danger outline and the warning mark, whose
    /// tooltip is the problem as the pin field words it.</summary>
    [AvaloniaFact]
    public void ProblemShowsOutlineAndMark()
    {
        using var s = Show();
        var sliders = Tiles<SliderTile>(s.Page);
        Assert.All(sliders, t => Assert.False(In<Border>(t, "ProblemMark").IsVisible));

        s.Vm.Controller.SetSliderPin(1, 33);   // Slider 1 is on GPIO 33
        Settle(s.Window);

        var first = In<Border>(sliders[0], "ProblemMark");
        var second = In<Border>(sliders[1], "ProblemMark");
        Assert.True(first.IsVisible);
        Assert.True(second.IsVisible);
        Assert.Equal(1, DrawnOpacity(second));
        Assert.Equal("Slider 1 also uses GPIO 33.", ToolTip.GetTip(second));
        Assert.Equal("Slider 2 also uses GPIO 33.", ToolTip.GetTip(first));
        foreach (var tile in sliders.Take(2))
            Assert.True(tile.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Classes.Contains("ProblemOutline")).IsVisible);
        Assert.False(In<Border>(sliders[2], "ProblemMark").IsVisible);
    }

    /// <summary>A control added with + Add draws dashed in the accent colour and says to save
    /// first, in place of what it controls.</summary>
    [AvaloniaFact]
    public void NewTileSaysSaveFirst()
    {
        using var s = Show();
        Assert.All(Tiles<SliderTile>(s.Page), t => Assert.False(In<TextBlock>(t, "NewText").IsVisible));

        s.Hardware.AddSliderCommand.Execute(null);
        Settle(s.Window);

        var sliders = Tiles<SliderTile>(s.Page);
        Assert.Equal(6, sliders.Count);
        var added = sliders[^1];
        var text = In<TextBlock>(added, "NewText");
        Assert.Equal("New · Save to controller to set it up", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        Assert.Equal(1, DrawnOpacity(text));
        Assert.False(In<Grid>(added, "LabelRow").IsVisible);
        Assert.False(In<Border>(added, "Grip").IsVisible);
        var outline = added.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Classes.Contains("NewOutline"));
        Assert.True(outline.IsVisible);
        Assert.False(sliders[0].GetVisualDescendants().OfType<Rectangle>().Single(r => r.Classes.Contains("NewOutline")).IsVisible);
        Assert.True(In<Grid>(sliders[0], "LabelRow").IsVisible);

        s.Hardware.AddButtonCommand.Execute(null);
        Settle(s.Window);
        var button = Tiles<ButtonTile>(s.Page)[^1];
        Assert.Equal("New · Save to controller to set it up", In<TextBlock>(button, "NewText").Text);
        Assert.True(In<TextBlock>(button, "NewText").IsEffectivelyVisible);
        Assert.False(In<Grid>(button, "ActionRows").IsVisible);
        Assert.False(In<TextBlock>(Tiles<ButtonTile>(s.Page)[0], "NewText").IsVisible);
    }

    /// <summary>Pins stay in the inspector: no tile draws a pin or an indicator's wiring, during a
    /// test either.</summary>
    [AvaloniaFact]
    public void NoPinsOnTiles()
    {
        using var s = Show();
        static void NoPins(Shown s)
        {
            foreach (var tile in AllTiles(s.Page))
            {
                Assert.DoesNotContain(tile.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("PinPill") && b.IsEffectivelyVisible);
                Assert.DoesNotContain(tile.GetVisualDescendants().OfType<TextBlock>(),
                                      t => t.IsEffectivelyVisible && DrawnOpacity(t) > 0 && (t.Text ?? "").StartsWith("GPIO"));
                Assert.DoesNotContain(tile.GetVisualDescendants().OfType<Control>(),
                                      c => c.Name is "WiringPin" or "WiringTitle" or "WiringIndicator");
            }
        }
        NoPins(s);

        s.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Hardware.IsTesting);
        NoPins(s);
    }

    /// <summary>During a test a slider shows its raw value in its label's place, and its fader the
    /// raw level.</summary>
    [AvaloniaFact]
    public void TestShowsRawValues()
    {
        using var s = Show();
        s.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Hardware.IsTesting);

        var slider = Tiles<SliderTile>(s.Page)[2];
        s.Rig.Connection!.RaiseMessage(new SliderMessage(2, 2310));
        var test = In<TextBlock>(slider, "WiringTest");
        Until(s.Window, () => test.Text == "2310");
        Assert.True(test.IsEffectivelyVisible);
        Assert.Equal(1, DrawnOpacity(test));
        Assert.Equal(0, DrawnOpacity(In<Grid>(slider, "LabelRow")));

        var fader = In<Panel>(slider, "Fader");
        var fill = In<Border>(slider, "LevelFill");
        Assert.Equal((fader.Bounds.Height - 6) * 2310 / 4095.0, fill.Bounds.Height, 0.5);
    }

    /// <summary>No I2C bus chips on the drawing, even with a screen on a bus.</summary>
    [AvaloniaFact]
    public void NoBusChips()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Buttons.Items[0]);
        Settle(s.Window);
        Click(s, In<RadioButton>(s.Host, "LightScreen"));   // a screen on bus 0
        Assert.Equal(Volco.Core.Controller.Proto.Button.IndicatorOneofCase.Screen, s.Vm.Controller.Draft.Buttons[0].IndicatorCase);

        Assert.DoesNotContain(s.Page.GetVisualDescendants().OfType<Control>(), c => c.Name is "BusChip" or "BusChipList");
        Assert.DoesNotContain(s.Page.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("BusChip")
                                                                                && b.Name != "ControllerSettingsChip");
    }
}
