using Volco.App.ViewModels;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Communication;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class SettingsViewModelTests
{
    private static readonly AudioDevice Speakers = new("spk", "Speakers", AudioFlow.Render, true);
    private static readonly AudioDevice Headphones = new("hp", "Headphones", AudioFlow.Render, false);

    [Fact]
    public async Task SettingsViewModel_DevicesChangedFromWorkerThread_MarshalsToUiThread()
    {
        var engine = new FakeAudioEngine { Devices = { Speakers } };
        using var rig = new TestRig(engine);
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var slider = vm.Sliders.Items[0]; // a card with a device picker to refresh
        Assert.Equal(new[] { SliderItemViewModel.CurrentDefaultOutput, "Speakers" }, slider.DeviceChoices);

        engine.Devices.Add(Headphones);
        await Task.Run(() => engine.RaiseDeviceChanged(DeviceChangeKind.Added, Headphones), TestContext.Current.CancellationToken);

        // Nothing mutated yet: the handler only queued work.
        Assert.True(ui.Pending > 0);
        Assert.DoesNotContain("Headphones", slider.DeviceChoices);

        ui.RunAll();

        Assert.Contains("Headphones", slider.DeviceChoices);
    }

    [Fact]
    public void TwoSettingsTrees_EachResolvePickedDevicesAgainstTheirOwnDeviceList()
    {
        // Same friendly name, different endpoint ids: a shared lookup would hand the first tree
        // the second tree's id.
        using var rigA = new TestRig(new FakeAudioEngine { Devices = { new AudioDevice("a-spk", "Speakers", AudioFlow.Render, true) } });
        using var rigB = new TestRig(new FakeAudioEngine { Devices = { new AudioDevice("b-spk", "Speakers", AudioFlow.Render, true) } });
        using var vmA = rigA.CreateSettings(new RecordingDispatcher());
        var sliderA = vmA.Sliders.Items[0];
        var pressA = vmA.Buttons.Items[0].Press;
        using var vmB = rigB.CreateSettings(new RecordingDispatcher());
        _ = vmB.Sliders.Items[0];
        _ = vmB.Buttons.Items[0];

        sliderA.IsDevice = true;
        sliderA.SelectedDevice = "Speakers";
        pressA.Device = "Speakers";

        Assert.Equal("a-spk", rigA.Config.Config.Sliders[0].Device?.Id);
        Assert.Equal("a-spk", rigA.Config.Config.Buttons[0].Default().SinglePress.Device?.Id);
    }

    [Fact]
    public void SettingsViewModel_SeedsLiveLevelsFromRuntime_WhenCreatedAfterSliderReports()
    {
        using var rig = new TestRig();
        const int index = 0;
        Assert.Null(rig.Runtime.GetSliderLevel(index));

        // The firmware only reports a slider when it moves: deliver a frame before any VM exists.
        rig.Connection!.RaiseMessage(new SliderMessage(index, 2048));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (rig.Runtime.GetSliderLevel(index) is null && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        var level = rig.Runtime.GetSliderLevel(index);
        Assert.NotNull(level);

        using var vm = rig.CreateSettings(new RecordingDispatcher());

        var item = vm.Sliders.Items.Single(i => i.Index == index);
        Assert.Equal((double)level!.Value, item.LiveLevel, 5);
    }

    [Fact]
    public async Task ConnectionViewModel_StatusFromWorkerThread_MarshalsAndUpdates()
    {
        using var rig = new TestRig();
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var connection = vm.Connection;
        Assert.Equal(LinkState.Searching, connection.Link.State);

        await Task.Run(() => rig.Connection!.SetStatus(TransportStatus.Open), TestContext.Current.CancellationToken);

        Assert.Equal(LinkState.Searching, connection.Link.State);
        ui.RunAll();
        Assert.Equal(LinkState.Waiting, connection.Link.State);
        Assert.Equal("Connecting…", connection.StatusText);   // the port is open; no Hello yet

        rig.Connection!.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Led }));
        ui.RunAll();
        Assert.Equal("Connected", connection.StatusText);
    }

    [Fact]
    public async Task SettingsViewModel_Dispose_UnsubscribesFromRuntime()
    {
        var engine = new FakeAudioEngine { Devices = { Speakers } };
        using var rig = new TestRig(engine);
        var ui = new RecordingDispatcher();
        var vm = rig.CreateSettings(ui);
        _ = vm.Connection;

        vm.Dispose();
        await Task.Run(() =>
        {
            engine.RaiseDeviceChanged(DeviceChangeKind.Added, Headphones);
            rig.Connection!.SetStatus(TransportStatus.Open);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, ui.Pending);
        Assert.Equal(LinkState.Searching, vm.Connection.Link.State);
    }

    [Fact]
    public async Task SettingsViewModel_QueuedWorkRunsAfterDispose_DoesNothingAndDisposeIsIdempotent()
    {
        var engine = new FakeAudioEngine { Devices = { Speakers } };
        using var rig = new TestRig(engine);
        var ui = new RecordingDispatcher();
        var vm = rig.CreateSettings(ui);
        var slider = vm.Sliders.Items[0];
        var connection = vm.Connection;

        engine.Devices.Add(Headphones);
        await Task.Run(() =>
        {
            engine.RaiseDeviceChanged(DeviceChangeKind.Added, Headphones);
            rig.Connection!.SetStatus(TransportStatus.Open);
        }, TestContext.Current.CancellationToken);
        Assert.True(ui.Pending > 0);

        vm.Dispose();
        var ex = Record.Exception(() =>
        {
            ui.RunAll();
            vm.Dispose();
        });

        Assert.Null(ex);
        Assert.DoesNotContain("Headphones", slider.DeviceChoices);
        Assert.Equal(LinkState.Searching, connection.Link.State);
    }

    [Fact]
    public async Task SettingsViewModel_ButtonPressFromWorkerThread_FlashesThatCard()
    {
        using var rig = new TestRig();
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var card = vm.Buttons.Items[1];

        await Task.Run(() => rig.Connection!.RaiseMessage(new ButtonMessage(1, ButtonState.Pressed)), TestContext.Current.CancellationToken);

        Assert.False(card.IsActive);
        ui.RunAll();
        Assert.True(card.IsActive);
        Assert.False(vm.Buttons.Items[0].IsActive);
    }

    [Fact]
    public void SettingsViewModel_SliderReport_UpdatesLevelAndFlashesThatCard()
    {
        using var rig = new TestRig();
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);

        rig.Connection!.RaiseMessage(new SliderMessage(2, 4095));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (ui.Pending == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        ui.RunAll();

        Assert.Equal(1.0, vm.Sliders.Items[2].LiveLevel, 3);
        Assert.True(vm.Sliders.Items[2].IsActive);
        Assert.False(vm.Sliders.Items[0].IsActive);
    }

    [Fact]
    public void SettingsViewModel_RuntimeModeChange_MovesTheLiveMarker()
    {
        using var rig = new TestRig();
        rig.Config.Config.Modes.Add(new ModeConfig { Id = "gaming", Name = "Gaming" });
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var buttons = vm.Buttons;
        Assert.Equal(AppConfig.DefaultModeId, buttons.LiveModeId);

        rig.Runtime.SetActiveMode("gaming");
        ui.RunAll();

        Assert.Equal("gaming", buttons.LiveModeId);
        Assert.True(buttons.Modes.Single(p => p.Id == "gaming").IsLive);
        Assert.False(buttons.Modes.Single(p => p.Id == AppConfig.DefaultModeId).IsLive);
    }
}
