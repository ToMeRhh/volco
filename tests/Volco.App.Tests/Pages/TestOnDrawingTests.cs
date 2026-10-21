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

/// <summary>Hardware-page spec §8: the hardware test runs on the drawing.</summary>
public sealed class TestOnDrawingTests : IDisposable
{
    public TestOnDrawingTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = true;

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

    /// <summary>The stage on Hardware over the DIY setup (5 sliders; buttons LED, LED, pair), in
    /// sync with the controller.</summary>
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
        page.Mode = StageMode.Hardware;
        Settle(window);
        return new Shown(page, host, window, rig, vm);
    }

    private static void StartTest(Shown s)
    {
        s.Hardware.StartTestCommand.Execute(null);
        Until(s.Window, () => s.Hardware.IsTesting);
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

    private static string TestText(Control tile) => In<TextBlock>(tile, "WiringTest").Text ?? "";

    private static void Click(Shown s, Control control)
    {
        control.BringIntoView();
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    private static byte[]? LastLed(Shown s, int button)
        => s.Rig.Connection!.SentFrames.LastOrDefault(f => f.Length == 6 && f[2] == 0x83 && f[3] == button);

    [AvaloniaFact]
    public void TestShowsRawValuesAndPresses()
    {
        using var s = Show();
        var caseBorder = In<Border>(s.Page, "Case");
        Assert.DoesNotContain("testing", caseBorder.Classes);
        Assert.False(In<Border>(s.Page, "TestBand").IsVisible);

        StartTest(s);
        Assert.True(s.Rig.Runtime.InTestMode);
        Assert.Contains("testing", caseBorder.Classes);
        Assert.True(In<Border>(s.Page, "TestBand").IsVisible);
        Assert.Equal("Testing: sliders and buttons don't change volume or run actions.",
                     In<TextBlock>(s.Page, "TestBandText").Text);

        var slider = Tiles<SliderTile>(s.Page)[2];
        Assert.True(In<TextBlock>(slider, "WiringTest").IsVisible);
        Assert.False(In<TextBlock>(slider, "WiringPin").IsEffectivelyVisible);
        s.Rig.Connection!.RaiseMessage(new SliderMessage(2, 2310));
        Until(s.Window, () => TestText(slider) == "2310");

        var led = Tiles<ButtonTile>(s.Page)[0];
        var cap = In<Ellipse>(led, "Cap");
        Assert.Equal("Off", TestText(led));
        s.Rig.Connection.RaiseMessage(new ButtonMessage(0, ButtonState.Pressed));
        Until(s.Window, () => cap.Classes.Contains("pressed"));
        Assert.Equal("On", TestText(led));
        s.Rig.Connection.RaiseMessage(new ButtonMessage(0, ButtonState.Released));
        Until(s.Window, () => !cap.Classes.Contains("pressed"));
        Assert.Equal("On", TestText(led));

        var pair = Tiles<ButtonTile>(s.Page)[2];
        for (int i = 0; i < 2; i++)
        {
            s.Rig.Connection.RaiseMessage(new ButtonMessage(2, ButtonState.Pressed));
            s.Rig.Connection.RaiseMessage(new ButtonMessage(2, ButtonState.Released));
        }
        Until(s.Window, () => TestText(pair) == "B");
        Assert.Contains("on", In<Ellipse>(pair, "TestLedB").Classes);
        Assert.DoesNotContain("on", In<Ellipse>(pair, "TestLedA").Classes);

        s.Hardware.StopTestCommand.Execute(null);
        Until(s.Window, () => !s.Hardware.IsTesting);
        Assert.False(s.Rig.Runtime.InTestMode);
        Assert.DoesNotContain("testing", caseBorder.Classes);
        Assert.True(In<TextBlock>(slider, "WiringPin").IsEffectivelyVisible);
        Assert.False(In<TextBlock>(slider, "WiringTest").IsVisible);
    }

    [AvaloniaFact]
    public void ClickingALightAdvancesIt()
    {
        using var s = Show();
        StartTest(s);

        var led = Tiles<ButtonTile>(s.Page)[0];
        Click(s, In<Button>(led, "TestLight"));
        Until(s.Window, () => TestText(led) == "On");
        Assert.Contains("on", In<Ellipse>(led, "TestLed").Classes);
        Until(s.Window, () => LastLed(s, 0)?.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(0, 1)) == true);
        Click(s, In<Button>(led, "TestLight"));
        Until(s.Window, () => TestText(led) == "Off");
        Until(s.Window, () => LastLed(s, 0)?.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(0, 0)) == true);

        var pair = Tiles<ButtonTile>(s.Page)[2];
        Click(s, In<Button>(pair, "TestLight"));
        Until(s.Window, () => TestText(pair) == "A");
        Click(s, In<Button>(pair, "TestLight"));
        Until(s.Window, () => TestText(pair) == "B");
        Until(s.Window, () => LastLed(s, 2)?.AsSpan().SequenceEqual(ProtocolEncoder.EncodeLed(2, 2)) == true);
        Assert.True(s.Rig.Runtime.InTestMode);
    }

    [AvaloniaFact]
    public void LeavingHardwareEndsTheTest()
    {
        using var s = Show();
        StartTest(s);

        s.Page.Mode = StageMode.Controls;
        Settle(s.Window);

        Assert.False(s.Rig.Runtime.InTestMode);
        Assert.False(s.Hardware.IsTesting);
        Assert.DoesNotContain("testing", In<Border>(s.Page, "Case").Classes);
        Assert.False(In<Border>(s.Page, "TestBand").IsVisible);
    }

    [AvaloniaFact]
    public void InspectorHiddenWhileTesting()
    {
        using var s = Show();
        s.Vm.Controls.Select(s.Vm.Sliders.Items[0]);
        Settle(s.Window);
        var inspected = In<ContentControl>(s.Host, "InspectedContent");
        var pin = s.Hardware.PinField(new PinOwner(PinOwnerKind.Slider, 0));
        Assert.True(inspected.IsVisible);
        Assert.True(pin.IsEditable);

        StartTest(s);
        Assert.False(inspected.IsVisible);
        Assert.False(pin.IsEditable);
        Assert.False(s.Vm.Controller.CanEdit);
        Assert.True(In<Volco.App.Views.Hardware.DeviceCard>(s.Host, "Card").IsVisible);

        s.Hardware.StopTestCommand.Execute(null);
        Until(s.Window, () => !s.Hardware.IsTesting);
        Assert.True(inspected.IsVisible);
        Assert.True(pin.IsEditable);
    }
}
