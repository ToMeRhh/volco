using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Hardware;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Hardware-page spec §3.3, §7.1, §7.2 on the stage: the setup chooser takes the case's
/// rectangle, and Compare is a modal card that Escape or a click outside closes.</summary>
public sealed class SetupChooserPageTests : IDisposable
{
    public SetupChooserPageTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = true;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(StagePage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The stage on Hardware over Volco's DIY copy, greeted with <paramref name="state"/>.</summary>
    private static Shown Show(ControllerConfigState state, uint crc)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new StagePage(vm.Controls) { Hardware = vm.Hardware };
        var window = new Window { Content = page, Width = 1100, Height = 720 };
        window.Show();
        page.Mode = StageMode.Hardware;
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, 5, DiyKinds));
        Until(window, () => vm.Controller.Banner.Kind != BannerKind.None || vm.Controller.InSync);
        return new Shown(page, window, rig, vm);
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

    [AvaloniaFact]
    public void TheChooserFillsTheCase_AndAPickBringsTheDrawingBack()
    {
        using var s = Show(ControllerConfigState.None, 0);
        Until(s.Window, () => s.Vm.Hardware.ShowChooser);
        var caseBorder = In<Border>(s.Page, "Case");
        var drawing = In<StackPanel>(s.Page, "Drawing");
        var chooser = caseBorder.GetVisualDescendants().OfType<SetupChooser>().Single();
        Assert.True(chooser.IsEffectivelyVisible);
        Assert.Equal(0, drawing.Opacity);
        Assert.False(drawing.IsEffectivelyEnabled);
        // In the case's own cell: inside its bounds, as wide as the drawing.
        var origin = chooser.TranslatePoint(default, caseBorder)!.Value;
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(chooser.Bounds.Width <= drawing.Bounds.Width + 0.5);

        var cards = s.Page.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ChooserCard").ToList();
        Assert.Equal(s.Vm.Hardware.Chooser.Cards.Count, cards.Count);
        Assert.True(cards[0].Classes.Contains("primary"));
        cards[0].Command!.Execute(null);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.ShowChooser);
        Assert.Equal(1, drawing.Opacity);
        Assert.True(drawing.IsEffectivelyEnabled);
        Assert.DoesNotContain(s.Page.GetVisualDescendants(), v => v is SetupChooser { IsEffectivelyVisible: true });
    }

    [AvaloniaFact]
    public void CompareIsAModalCard_EscapeAndAClickOutsideCloseIt()
    {
        using var s = Show(ControllerConfigState.Stored, 0x1234);
        Assert.Equal(BannerKind.DifferentSetup, s.Vm.Controller.Banner.Kind);
        var overlay = In<Border>(s.Page, "CompareOverlay");
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenCompare();
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        Assert.Equal("The controller has a different setup", In<TextBlock>(s.Page, "CompareTitle").Text);
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsCompareOpen);
        Assert.False(overlay.IsVisible);

        s.Vm.Hardware.OpenCompare();
        Settle(s.Window);
        Assert.True(overlay.IsVisible);
        s.Window.MouseDown(new Point(4, 4), MouseButton.Left);
        s.Window.MouseUp(new Point(4, 4), MouseButton.Left);
        Settle(s.Window);
        Assert.False(s.Vm.Hardware.IsCompareOpen);
    }
}
