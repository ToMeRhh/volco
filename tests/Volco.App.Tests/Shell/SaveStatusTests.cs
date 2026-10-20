using Volco.App.ViewModels;
using Volco.Core.Config;
using Xunit;

namespace Volco.App.Tests.Shell;

public sealed class SaveStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"volco-save-{Guid.NewGuid()}");
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public SaveStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void UnsavedAndFailed_DifferInWordsAndColour()
    {
        Assert.Equal("Unsaved changes", SaveText.Footer(ConfigSaveState.Unsaved));
        Assert.Equal("Settings not saved", SaveText.Footer(ConfigSaveState.Failed));
        Assert.Equal(LinkTone.Neutral, SaveText.Tone(ConfigSaveState.Unsaved));
        Assert.Equal(LinkTone.Danger, SaveText.Tone(ConfigSaveState.Failed));
        Assert.Contains("disk is full", SaveText.ToolTip(ConfigSaveState.Failed, "The disk is full"));
        Assert.Equal("", SaveText.Footer(ConfigSaveState.Saved));
    }

    [Fact]
    public void Tray_AddsTheSaveLine_AndAFailureAsksForAttention()
    {
        Assert.Equal("Volco — Disconnected", TrayStatus.WithSaveLine("Volco — Disconnected", ConfigSaveState.Saved));
        Assert.Equal("Volco — Disconnected\nUnsaved changes", TrayStatus.WithSaveLine("Volco — Disconnected", ConfigSaveState.Unsaved));
        Assert.Equal("Volco — Disconnected\nSettings not saved: open Volco for details",
                     TrayStatus.WithSaveLine("Volco — Disconnected", ConfigSaveState.Failed));
        Assert.False(TrayStatus.SaveNeedsAttention(ConfigSaveState.Unsaved));
        Assert.True(TrayStatus.SaveNeedsAttention(ConfigSaveState.Failed));
    }

    [Fact]
    public async Task Sidebar_ShowsAFailingSave_WithRetry_UntilASaveWorks()
    {
        var config = new ConfigManager(ConfigPath);
        config.Load();
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        var ui = new RecordingDispatcher();
        using var vm = new SaveStatusViewModel(config, ui);
        Assert.False(vm.IsVisible);

        Directory.CreateDirectory(ConfigPath);        // a folder in the way: writes fail
        config.RequestSave();
        await WaitFor(ui, () => vm.State == ConfigSaveState.Unsaved);
        Assert.True(vm.IsVisible);
        Assert.False(vm.CanRetry);

        vm.RetryCommand.Execute(null);                // a second failure
        await WaitFor(ui, () => vm.State == ConfigSaveState.Failed);
        Assert.Equal("Settings not saved", vm.Text);
        Assert.Equal(LinkTone.Danger, vm.Tone);
        Assert.True(vm.CanRetry);
        Assert.StartsWith("Volco can't write its settings file:", vm.ToolTip);

        Directory.Delete(ConfigPath);
        vm.RetryCommand.Execute(null);
        await WaitFor(ui, () => vm.State == ConfigSaveState.Saved);
        Assert.False(vm.IsVisible);
    }

    private static async Task WaitFor(RecordingDispatcher ui, Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            ui.RunAll();
            if (condition()) return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Timed out");
    }
}
