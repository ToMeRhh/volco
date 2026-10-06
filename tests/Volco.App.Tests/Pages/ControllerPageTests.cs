using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Communication;
using Volco.Core.Controller;
using ControllerConfig = Volco.Core.Controller.Proto.ControllerConfig;
using Xunit;

namespace Volco.App.Tests.Pages;

public class ControllerPageTests
{
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(),
        ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId)));

    private static (ControllerPage Page, TestRig Rig, SettingsViewModel Vm) Show(byte[]? mirror = null)
    {
        var rig = new TestRig();
        if (mirror is not null) rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(mirror);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ControllerPage(vm.Controller);
        new Window { Content = page, Width = 900, Height = 1200 }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static void Greet(TestRig rig, ControllerConfigState state, uint crc)
    {
        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", state, crc, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void NotConnected_TheEditorIsHidden_WithAPlaceholder()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Assert.False(page.FindControl<StackPanel>("EditorSections")!.IsVisible);
        Assert.True(page.FindControl<TextBlock>("EditorPlaceholderText")!.IsVisible);

        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        Assert.True(page.FindControl<StackPanel>("EditorSections")!.IsVisible);
        Assert.False(page.FindControl<TextBlock>("EditorPlaceholderText")!.IsVisible);
    }

    [AvaloniaFact]
    public void InSync_AnEditShowsTheFooter_RevertHidesIt()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        var footer = page.FindControl<Border>("Footer")!;
        Assert.False(footer.IsVisible);

        vm.Controller.Sliders[0].Pin = 39;
        Dispatcher.UIThread.RunJobs();
        Assert.True(footer.IsVisible);
        Assert.True(page.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);

        page.FindControl<Button>("RevertButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(footer.IsVisible);
    }

    [AvaloniaFact]
    public void TheBannerButtonsFollowItsActions()
    {
        var (page, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.None, 0);
        Assert.True(page.FindControl<Border>("BannerPanel")!.IsVisible);
        Assert.Equal("Set up your controller.", page.FindControl<TextBlock>("BannerText")!.Text);
        Assert.True(page.FindControl<Button>("SetUpButton")!.IsVisible);
        Assert.False(page.FindControl<Button>("ApplyMineButton")!.IsVisible);
        Assert.False(page.FindControl<Button>("FixItButton")!.IsVisible);
    }

    [AvaloniaFact]
    public void Behaviour_OnlyWithAScreenButton_AdvancedCollapsed()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        Assert.True(page.FindControl<TextBlock>("NoScreensText")!.IsVisible);
        Assert.False(page.FindControl<StackPanel>("BehaviourBody")!.IsVisible);
        Assert.False(page.FindControl<Border>("AdvancedSection")!.IsVisible);

        vm.Controller.Buttons[0].IndicatorIndex = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.FindControl<StackPanel>("BehaviourBody")!.IsVisible);
        Assert.Single(page.GetVisualDescendants().OfType<NumericUpDown>(), n => n.Name == "SdaBox");
    }

    [AvaloniaFact]
    public void TheWindow_HasAControllerEntry_LeavingItEndsTheTest_AndTheDotFollowsTheVm()
    {
        using var rig = new TestRig();
        rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { AllowClose = true };
        window.Show();
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));

        window.ShowPage("controller");
        Assert.IsType<ControllerPage>(window.CurrentPage);
        vm.Controller.StartTestCommand.Execute(null);
        Assert.True(rig.Runtime.InTestMode);
        window.ShowPage("general");
        Assert.False(rig.Runtime.InTestMode);

        var nav = window.FindControl<ListBox>("NavList")!;
        var item = nav.Items.Cast<NavItem>().Single(n => n.Tag == "controller");
        Assert.False(item.ShowDot);
        vm.ControllerNavDot = true;
        Assert.True(item.ShowDot);
        window.Close();
    }
}
