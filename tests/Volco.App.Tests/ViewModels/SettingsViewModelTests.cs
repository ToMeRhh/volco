using Volco.Core.Audio;
using Volco.Core.Communication;
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
        var outputs = vm.OutputDevices; // construct the tab VM so it has a picker to refresh
        Assert.Equal(new[] { "Speakers" }, outputs.OutputDevices.Select(d => d.FriendlyName));

        engine.Devices.Add(Headphones);
        await Task.Run(() => engine.RaiseDeviceChanged(DeviceChangeKind.Added, Headphones));

        // Nothing mutated yet: the handler only queued work.
        Assert.True(ui.Pending > 0);
        Assert.Equal(new[] { "Speakers" }, outputs.OutputDevices.Select(d => d.FriendlyName));

        ui.RunAll();

        Assert.Contains(outputs.OutputDevices, d => d.FriendlyName == "Headphones");
        Assert.Contains("Headphones", outputs.OutputDeviceNames);
    }

    [Fact]
    public async Task ConnectionViewModel_StatusFromWorkerThread_MarshalsAndUpdates()
    {
        using var rig = new TestRig();
        var ui = new RecordingDispatcher();
        using var vm = rig.CreateSettings(ui);
        var connection = vm.Connection;
        Assert.Equal(ConnectionStatus.Disconnected, connection.Status);

        await Task.Run(() => rig.Connection!.SetStatus(ConnectionStatus.Connected));

        Assert.Equal(ConnectionStatus.Disconnected, connection.Status);
        ui.RunAll();
        Assert.Equal(ConnectionStatus.Connected, connection.Status);
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
            rig.Connection!.SetStatus(ConnectionStatus.Connected);
        });

        Assert.Equal(0, ui.Pending);
        Assert.Equal(ConnectionStatus.Disconnected, vm.Connection.Status);
    }
}
