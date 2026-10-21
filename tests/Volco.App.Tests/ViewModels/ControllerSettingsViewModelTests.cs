using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;
using Volco.Core.Tests.Runtime;
using Volco.App.Views.Hardware;
using Xunit;

namespace Volco.App.Tests.ViewModels;

/// <summary>Hardware-page spec §6.6: the controller's settings in plain words over the raw fields.</summary>
public sealed class ControllerSettingsViewModelTests : IDisposable
{
    private readonly TestRig _rig = new();
    private readonly RecordingDispatcher _ui = new();
    private SettingsViewModel? _settings;
    private ControllerSettingsViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        _settings?.Dispose();
        _rig.Dispose();
    }

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));

    private ControllerViewModel Controller => _settings!.Controller;

    private ControllerSettingsViewModel SetUp(bool screen = true)
    {
        _rig.Config.Config.Sliders = Enumerable.Range(0, 5).Select(i => new Volco.Core.Config.SliderConfig { Index = i, Name = $"s{i}" }).ToList();
        _rig.Config.Config.Buttons = Enumerable.Range(0, 3).Select(i => new Volco.Core.Config.ButtonConfig { Index = i }).ToList();
        _rig.Config.Config.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        _settings = _rig.CreateSettings(_ui);
        _ = _settings.Controls;
        var conn = _rig.Connection!;
        conn.SetStatus(TransportStatus.Open);
        conn.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
            ControllerConfigCodec.Crc32(DiyBlob), 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        _ui.RunAll();
        Assert.True(Controller.CanEdit);
        if (screen)
        {
            Controller.Draft.Buttons[0].Screen = new Screen();
            Controller.OnDraftEdited();
        }
        return _vm = new ControllerSettingsViewModel(Controller, () => { }, _settings!.Hardware.PinField);
    }

    [Fact]
    public void BrightnessRoundTrips()
    {
        var vm = SetUp();
        Controller.Brightness = 255;
        Assert.Equal(100, vm.BrightnessPct);
        vm.BrightnessPct = 50;
        Assert.Equal(128m, Controller.Brightness);
        Assert.Equal(50, vm.BrightnessPct);
    }

    [Fact]
    public void NeverTurnsDimOff()
    {
        var vm = SetUp();
        Assert.True(Controller.DimEnabled);
        vm.DimAfterIndex = 0;
        Assert.False(Controller.DimEnabled);
        Assert.Equal(0, vm.DimAfterIndex);
        vm.DimAfterIndex = 3;   // 5 minutes
        Assert.True(Controller.DimEnabled);
        Assert.Equal(300m, Controller.DimAfterS);
    }

    [Fact]
    public void OffListValueIsCustomAndKept()
    {
        var vm = SetUp();
        Controller.OffEnabled = true;
        Controller.OffAfterS = 90;
        Assert.Equal("Custom (90 s)", vm.OffAfter[vm.OffAfterIndex].Label);
        Assert.Equal(90m, Controller.OffAfterS);
        Assert.Equal("Custom (90 s)", vm.OffAfter[vm.OffAfterIndex].Label);
    }

    [Fact]
    public void InputFeelPairs()
    {
        var vm = SetUp();
        vm.InputFeel = 1;
        Assert.Equal(20m, Controller.SmoothingPct);
        Assert.Equal(25m, Controller.Deadband);
        Assert.Equal("Normal", vm.InputSummary);
        vm.InputFeel = 0;
        Assert.Equal((40m, 15m), (Controller.SmoothingPct, Controller.Deadband));
        vm.InputFeel = 2;
        Assert.Equal((10m, 40m), (Controller.SmoothingPct, Controller.Deadband));
        vm.InputFeel = 1;
        Controller.Deadband = 30;
        Assert.Equal(3, vm.InputFeel);
        Assert.Equal("Custom", vm.InputSummary);
    }

    [Fact]
    public void ScreensHiddenWithoutAScreen()
    {
        var vm = SetUp(screen: false);
        Assert.False(vm.ShowScreens);
        Assert.False(vm.IsScreensOpen);
        Assert.Equal(ControllerSettingsViewModel.InputSection, vm.OpenSection);
    }

    [Fact]
    public void OneSectionOpenAtATime()
    {
        var vm = SetUp();
        Assert.Equal(ControllerSettingsViewModel.ScreensSection, vm.OpenSection);
        vm.ToggleSection(ControllerSettingsViewModel.InputSection);
        Assert.True(vm.IsInputOpen);
        Assert.False(vm.IsScreensOpen);
        vm.ToggleSection(ControllerSettingsViewModel.InputSection);
        Assert.False(vm.IsInputOpen);
    }

    [Fact]
    public void SummariesAndDimmedTo()
    {
        var vm = SetUp();
        Controller.Brightness = 204;
        Controller.OffEnabled = true;
        Controller.OffAfterS = 1800;
        Assert.Equal("80%, off after 30 min", vm.ScreensSummary);
        Assert.Equal("Off", vm.WifiSummary);
        Controller.UdpEnabled = true;
        Controller.Ssid = "HomeNet";
        Assert.Equal("On · HomeNet", vm.WifiSummary);
        Controller.DimBrightness = 8;
        Assert.Equal(0, vm.DimmedToIndex);
        Controller.DimBrightness = 100;
        Assert.Equal("Custom (100)", vm.DimmedTo[vm.DimmedToIndex]);
        Assert.Equal(100m, Controller.DimBrightness);
    }

    [Fact]
    public void SelectingTheSettingsClearsTheTileSelection()
    {
        SetUp();
        var hardware = _settings!.Hardware;
        _settings.Controls.Select(_settings.Controls.Sliders.Items[0]);
        hardware.SelectControllerSettings();
        Assert.IsType<ControllerSettingsViewModel>(hardware.Inspected);
        Assert.Null(_settings.Controls.Selected);
        _settings.Controls.Select(_settings.Controls.Sliders.Items[1]);
        Assert.IsType<SliderWiringViewModel>(hardware.Inspected);
    }

    [Fact]
    public void ListsKeepTheirInstanceUntilTheirContentsChange()
    {
        var vm = SetUp();
        Controller.OffEnabled = true;
        Controller.OffAfterS = 90;
        Controller.DimBrightness = 100;
        var off = vm.OffAfter;
        var dimmed = vm.DimmedTo;
        Controller.Brightness = 100;   // unrelated
        Assert.Same(off, vm.OffAfter);
        Assert.Same(dimmed, vm.DimmedTo);
        Controller.OffAfterS = 91;
        Assert.NotSame(off, vm.OffAfter);
        Assert.True(vm.OffAfter[vm.OffAfterIndex].IsCustom);
    }

    [Fact]
    public void RefusedFeelEditRaisesTheSegments()
    {
        var vm = SetUp();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.InputFeel = 3;
        Assert.Contains(nameof(ControllerSettingsViewModel.IsFeelLow), raised);
        Assert.Contains(nameof(ControllerSettingsViewModel.IsFeelNormal), raised);
        Assert.Contains(nameof(ControllerSettingsViewModel.IsFeelHigh), raised);
    }

    [AvaloniaFact]
    public void BoxesKeepTheirSelectionAfterAnUnrelatedChange()
    {
        var vm = SetUp();
        Controller.DimBrightness = 100;
        var view = new ControllerSettingsInspector { DataContext = vm };
        var window = new Window { Content = view, Width = 500, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "DimmedToBox");
        Assert.Equal(2, box.SelectedIndex);
        Controller.Brightness = 100;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, box.SelectedIndex);
        window.Close();
    }
}
