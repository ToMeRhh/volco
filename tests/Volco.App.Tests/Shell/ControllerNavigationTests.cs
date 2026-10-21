using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Inspector;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Shell;

/// <summary>
/// Controller-page spec §2, §4: the sidebar has General and Controller, and the Controller page is
/// one page with no modes: its title row's buttons and the mode tabs together, the leave prompt
/// for an unsaved draft, and the selected tile's inspector after General.
/// </summary>
public sealed class ControllerNavigationTests : IDisposable
{
    public ControllerNavigationTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(SettingsWindow Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public NavItem[] Items => Window.FindControl<ListBox>("NavList")!.Items.Cast<NavItem>().ToArray();
        public string NavTag => ((NavItem)Window.FindControl<ListBox>("NavList")!.SelectedItem!).Tag;

        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The settings window on General over the DIY setup (5 sliders; buttons LED, LED,
    /// pair), in sync with the controller so the draft can be edited.</summary>
    private static Shown Show()
    {
        var blob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(blob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                                                     ControllerConfigCodec.Crc32(blob), 5, DiyKinds));
        Until(window, () => vm.Controller.CanEdit);
        return new Shown(window, rig, vm);
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

    /// <summary>Runs the dispatcher for <paramref name="ms"/> so the crossfades end.</summary>
    private static void Pump(Window window, int ms = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Settle(window);
            Thread.Sleep(10);
        }
        Settle(window);
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>The opacity it is drawn with: its own times its ancestors'.</summary>
    private static double DrawnOpacity(Visual visual)
    {
        var opacity = 1.0;
        for (var v = visual; v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        return opacity;
    }

    private static StagePage Stage(Shown s) => Assert.IsType<StagePage>(s.Window.CurrentPage);

    [AvaloniaFact]
    public void SidebarHasGeneralAndController()
    {
        using var s = Show();
        Assert.Equal(new[] { "General", "Controller" }, s.Items.Select(n => n.Label));
        Assert.Equal(new[] { "general", "controller" }, s.Items.Select(n => n.Tag));
        Assert.True(Application.Current!.TryFindResource("Icon.Sliders", out var sliders));
        Assert.Same(sliders, s.Items[1].Icon);

        // The nav dot (a firmware update) is on the Controller entry.
        Assert.False(s.Items[1].ShowDot);
        s.Vm.ControllerNavDot = true;
        Assert.True(s.Items[1].ShowDot);
    }

    [AvaloniaFact]
    public void ShowPageControlsOpensController()
    {
        using var s = Show();
        Assert.IsType<GeneralPage>(s.Window.CurrentPage);

        s.Window.ShowPage("controls");
        Settle(s.Window);
        Assert.Equal("controller", s.NavTag);
        var stage = Stage(s);
        Assert.Same(s.Vm.Hardware, stage.Hardware);

        s.Window.ShowPage("general");
        Settle(s.Window);
        s.Window.ShowPage("controller");
        Settle(s.Window);
        Assert.Same(stage, s.Window.CurrentPage);   // the same page
    }

    [AvaloniaFact]
    public void ControllerShowsTitleActionsAndTheModeTabs()
    {
        using var s = Show();
        s.Window.ShowPage("controller");
        Settle(s.Window);
        var stage = Stage(s);

        Assert.Equal("Controller", In<TextBlock>(stage, "PageTitle").Text);
        foreach (var name in new[] { "AddButton", "ControllerSettingsChip", "TestButton" })
            Assert.True(In<Button>(stage, name).IsEffectivelyVisible, name);

        var tabs = In<ItemsControl>(stage, "ModeTabs");
        Assert.True(tabs.IsEffectivelyVisible);
        Assert.True(tabs.IsEffectivelyEnabled);
        Assert.Equal(1, DrawnOpacity(tabs));
        Assert.True(In<Button>(stage, "AddModeButton").IsEffectivelyEnabled);
        Assert.All(stage.GetVisualDescendants().OfType<RadioButton>().Where(r => r.Name == "ModeTab"),
                   tab => Assert.True(tab.IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void LeavingWithADirtyDraftAsks()
    {
        using var s = Show();
        s.Window.ShowPage("controller");
        Settle(s.Window);
        var stage = Stage(s);
        s.Vm.Controller.SetSliderPin(1, 39);
        Settle(s.Window);

        s.Window.ShowPage("general");
        Settle(s.Window);
        Assert.NotNull(s.Vm.Hardware.LeavePrompt);
        Assert.True(In<Border>(stage, "LeaveOverlay").IsVisible);
        Assert.Equal("controller", s.NavTag);
        Assert.Same(stage, s.Window.CurrentPage);
    }

    /// <summary>With no copy of the controller's setup the draft is the Blank placeholder: it holds
    /// nothing the user changed, so leaving doesn't ask, and the drawing is Volco's controls.</summary>
    [AvaloniaFact]
    public void WithNoControllerCopy_TheDrawingIsVolcosControls_AndLeavingDoesntAsk()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1100, Height = 720 };
        window.Show();
        try
        {
            window.ShowPage("controller");
            Settle(window);
            Assert.True(vm.Controller.IsPlaceholder);
            Assert.False(vm.Hardware.HasUnsavedChanges);
            var stage = Assert.IsType<StagePage>(window.CurrentPage);
            Assert.True(vm.Sliders.Items.Count > 0);
            Assert.Equal(vm.Sliders.Items.Count + vm.Buttons.Items.Count, stage.Tiles().Count);
            Assert.False(vm.Hardware.ShowSaveBar);
            Assert.False(In<Volco.App.Views.Hardware.SaveBar>(stage, "SaveBarHost").IsVisible);   // nothing the user changed

            window.ShowPage("general");
            Settle(window);
            Assert.Null(vm.Hardware.LeavePrompt);
            Assert.IsType<GeneralPage>(window.CurrentPage);
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SidePanelAfterGeneralShowsTheInspector()
    {
        using var s = Show();
        s.Window.ShowPage("controller");
        Settle(s.Window);
        s.Vm.Controls.Select(s.Vm.Buttons.Items[1]);
        Settle(s.Window);

        StageTransitions.Enabled = true;
        s.Window.ShowPage("general");
        Pump(s.Window);
        s.Window.ShowPage("controller");
        Pump(s.Window);

        var stage = Stage(s);
        var content = In<ContentControl>(stage, "InspectorContent");
        var inspector = content.GetVisualDescendants().OfType<ButtonInspector>().Single();
        Assert.Same(s.Vm.Buttons.Items[1], inspector.DataContext);
        Assert.True(inspector.IsEffectivelyVisible);
        Assert.Equal(1, DrawnOpacity(inspector), 3);
        Assert.Equal("Button 2", In<TextBlock>(inspector, "HeadingText").Text);
    }
}
