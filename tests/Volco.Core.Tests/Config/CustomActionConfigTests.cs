using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

/// <summary>Schema v6: the media, program and script action fields (custom-actions spec §2.1).</summary>
public class CustomActionConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public CustomActionConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void ScriptAndMediaActions_RoundTrip()
    {
        var cm = new ConfigManager(_configPath);
        cm.Load();
        var binding = cm.Config.Buttons[0].Default();
        binding.SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.RunScript,
            Command = new CommandConfig
            {
                Path = @"C:\My Scripts\backup now.bat", Arguments = "--fast \"a b\"",
                WorkingDirectory = @"D:\work", Name = "Backup",
            },
            WhileRunning = WhileRunningPolicy.StartAnother,
        };
        binding.LongPress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause,
            MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        };
        cm.Save();

        var json = File.ReadAllText(_configPath);
        Assert.Contains("\"action\": \"RunScript\"", json);
        Assert.Contains("\"whileRunning\": \"StartAnother\"", json);

        var again = new ConfigManager(_configPath);
        again.Load();
        var b = again.Config.Buttons[0].Default();
        Assert.Equal(ButtonActionType.RunScript, b.SinglePress.Action);
        Assert.Equal(@"C:\My Scripts\backup now.bat", b.SinglePress.Command!.Path);
        Assert.Equal("--fast \"a b\"", b.SinglePress.Command.Arguments);
        Assert.Equal(@"D:\work", b.SinglePress.Command.WorkingDirectory);
        Assert.Equal("Backup", b.SinglePress.Command.Name);
        Assert.Equal(WhileRunningPolicy.StartAnother, b.SinglePress.WhileRunning);
        Assert.Equal(ButtonActionType.MediaPlayPause, b.LongPress.Action);
        Assert.Equal("Spotify.exe", b.LongPress.MediaApp!.Id);
        Assert.Equal("Spotify", b.LongPress.MediaApp.Name);
        Assert.Equal(WhileRunningPolicy.Ignore, b.LongPress.WhileRunning);
    }

    [Fact]
    public void Load_V5_StampsV6_WithBackup()
    {
        const string v5 = """
        { "schemaVersion": 5, "connection": { "type": "Serial", "serial": { "comPort": "COM7" } },
          "sliders": [], "buttons": [ { "index": 0, "actions": { "default": { "singlePress": { "action": "MuteCurrentOutput" } } } } ],
          "presets": [ { "id": "default", "name": "Default" } ] }
        """;
        File.WriteAllText(_configPath, v5);

        var cm = new ConfigManager(_configPath);
        cm.Load();

        Assert.Equal(6, cm.Config.SchemaVersion);
        var action = cm.Config.Buttons[0].Default().SinglePress;
        Assert.Equal(ButtonActionType.MuteCurrentOutput, action.Action);
        Assert.Null(action.Command);
        Assert.Null(action.MediaApp);
        Assert.Contains("\"schemaVersion\": 6", File.ReadAllText(_configPath));
        Assert.Equal(v5, File.ReadAllText(Path.Combine(_tempDir, "config.v5.bak")));
    }

    [Theory]
    [InlineData(ButtonActionType.MediaPlayPause, true, false)]
    [InlineData(ButtonActionType.MediaNext, true, false)]
    [InlineData(ButtonActionType.MediaPrevious, true, false)]
    [InlineData(ButtonActionType.MediaStop, true, false)]
    [InlineData(ButtonActionType.OpenProgram, false, true)]
    [InlineData(ButtonActionType.RunScript, false, true)]
    [InlineData(ButtonActionType.MuteCurrentOutput, false, false)]
    [InlineData(ButtonActionType.NextPreset, false, false)]
    public void ActionGroups(ButtonActionType type, bool media, bool runsCode)
    {
        Assert.Equal(media, ButtonActionTypes.IsMedia(type));
        Assert.Equal(runsCode, ButtonActionTypes.RunsCode(type));
    }
}
