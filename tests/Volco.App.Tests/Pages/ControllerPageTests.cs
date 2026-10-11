using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.Core.Communication;
using Volco.Core.Config;
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
        rig.Connection!.SetStatus(TransportStatus.Open);
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
        Assert.Equal("Unsaved changes", page.FindControl<StatusLine>("ProgressText")!.Text);
        Assert.True(footer.Bounds.Width < 700);   // a snackbar that fits its text, not a full-width bar
        Assert.True(page.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);

        page.FindControl<Button>("RevertButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(footer.IsVisible);
    }

    [AvaloniaFact]
    public void APinConflict_MarksBothRows_WithTheErrorLineAndARedBox()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        vm.Controller.Sliders[0].Pin = 39;
        vm.Controller.Sliders[1].Pin = 39;
        Dispatcher.UIThread.RunJobs();

        var rows = page.FindControl<ItemsControl>("SliderRows")!;
        var errors = rows.GetVisualDescendants().OfType<StatusLine>().Where(l => l.Name == "RowError" && l.IsVisible).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(LinkTone.Danger, e.Tone));
        var boxes = rows.GetVisualDescendants().OfType<NumericUpDown>().Where(n => n.Name == "PinBox").ToList();
        Assert.Equal(new[] { true, true, false, false, false }, boxes.Select(b => b.Classes.Contains("Invalid")).ToArray());
    }

    [AvaloniaFact]
    public void TheTest_RunsInADialog_EscapeOrAClickOutsideEndsIt()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        var overlay = page.FindControl<Border>("DialogOverlay")!;
        var dialog = page.FindControl<StackPanel>("TestDialog")!;

        page.FindControl<Button>("StartTestButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible);
        Assert.True(dialog.IsVisible);
        Assert.Null(page.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name is "TestLedCombo" or "TestPairCombo"));

        page.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Runtime.InTestMode);
        Assert.False(overlay.IsVisible);

        page.FindControl<Button>("StartTestButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Runtime.InTestMode);
        var window = (Window)TopLevel.GetTopLevel(page)!;
        window.MouseDown(new Avalonia.Point(5, 5), MouseButton.Left);   // on the scrim, outside the card
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Runtime.InTestMode);

        page.FindControl<Button>("StartTestButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.FindControl<Button>("StopTestButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Runtime.InTestMode);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void TheConnectionSection_IsOnThePage()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Assert.NotNull(page.FindControl<ComboBox>("PortCombo"));
        Assert.Null(page.FindControl<ComboBox>("FirmwarePortCombo"));   // one port picker for both
        page.FindControl<RadioButton>("UdpOption")!.IsChecked = true;
        Assert.Equal(ConnectionType.Udp, rig.Config.Config.Connection.Type);
        Assert.False(page.FindControl<WrapPanel>("FirmwareButtons")!.IsVisible);   // firmware is USB only
    }

    [AvaloniaFact]
    public void TheFirmwareLine_HasNoIcon_WhenNothingIsWrong()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        var line = page.FindControl<StatusLine>("FirmwareText")!;
        Assert.Equal("Development firmware", line.Text);
        Assert.False(line.ShowIcon);
        Assert.False(line.GetVisualDescendants().OfType<PathIcon>().Single().IsVisible);
    }

    [AvaloniaFact]
    public void ScreenTimes_TakeANumberWithOrWithoutTheUnit_DownToOneSecond()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.Stored, ControllerConfigCodec.Crc32(DiyBlob));
        var box = page.FindControl<NumericUpDown>("DimAfterBox")!;

        box.Text = "5";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5m, vm.Controller.DimAfterS);

        box.Text = "30s";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(30m, vm.Controller.DimAfterS);

        box.Text = "1 s";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1m, vm.Controller.DimAfterS);
        Assert.Null(vm.Controller.BehaviourError);
    }

    [AvaloniaFact]
    public void TheConnectionCard_ComesBeforeTheStatusCard()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        var cards = page.GetVisualDescendants().OfType<Border>().Where(b => b.Name is "ConnectionPanel" or "StatusPanel")
                        .Select(b => b.Name).ToArray();
        Assert.Equal(new[] { "ConnectionPanel", "StatusPanel" }, cards);
    }

    [AvaloniaFact]
    public void TheBannerButtonsFollowItsActions()
    {
        var (page, rig, vm) = Show();
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.None, 0);
        Assert.True(page.FindControl<Border>("BannerPanel")!.IsVisible);
        Assert.Equal("This controller isn't set up yet.", page.FindControl<StatusLine>("BannerText")!.Text);
        Assert.False(page.FindControl<Button>("SetUpButton")!.IsVisible);   // in the setup dialog instead
        Assert.False(page.FindControl<Button>("ApplyMineButton")!.IsVisible);
        Assert.False(page.FindControl<Button>("FixItButton")!.IsVisible);
    }

    [AvaloniaFact]
    public void ANotSetUpController_GetsTheSetupDialog_OverThePage()
    {
        var (page, rig, vm) = Show(DiyBlob);
        using var _ = rig;
        using var __ = vm;
        Greet(rig, ControllerConfigState.None, 0);
        Assert.True(page.FindControl<Border>("DialogOverlay")!.IsVisible);
        Assert.True(page.FindControl<StackPanel>("SetupDialog")!.IsVisible);
        Assert.False(page.FindControl<StackPanel>("InstallDialog")!.IsVisible);
        Assert.True(page.FindControl<Button>("SetupApplySavedButton")!.IsVisible);
        Assert.False(page.FindControl<Button>("OpenSetupButton")!.IsVisible);

        page.FindControl<Button>("CloseSetupButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(page.FindControl<Border>("DialogOverlay")!.IsVisible);
        Assert.True(page.FindControl<Button>("OpenSetupButton")!.IsVisible);

        page.FindControl<Button>("OpenSetupButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.FindControl<Button>("SetupFromPresetButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(page.FindControl<Border>("DialogOverlay")!.IsVisible);
        Assert.True(page.FindControl<Border>("SetupGuidePanel")!.IsVisible);
        Assert.True(page.FindControl<Border>("Footer")!.IsVisible);
        Assert.True(page.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
        Assert.Equal("This setup isn't on the controller yet.", page.FindControl<StatusLine>("ProgressText")!.Text);
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
