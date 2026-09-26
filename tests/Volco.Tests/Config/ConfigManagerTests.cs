using System.Text.Json;
using Volco.Config;
using Xunit;

namespace Volco.Tests.Config;

public class ConfigManagerTests
{
    [Fact]
    public void AppConfig_RoundTrips_ThroughJson()
    {
        var config = new AppConfig
        {
            Connection = new ConnectionConfig
            {
                Type = ConnectionType.Serial,
                Serial = new SerialConfig { ComPort = "COM3", BaudRate = 9600 },
                Udp = new UdpConfig { ListenPort = 12345 }
            },
            Sliders = new List<SliderConfig>
            {
                new() { Index = 0, Type = SliderMappingType.Processes, Processes = new List<string> { "spotify.exe" }, Invert = false },
                new() { Index = 1, Type = SliderMappingType.Device, Invert = false },
                new() { Index = 2, Type = SliderMappingType.Unmapped, Invert = true },
            },
            Buttons = new List<ButtonConfig>
            {
                new()
                {
                    Index = 0,
                    SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput },
                    DoublePress = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice },
                    LongPress = new ButtonActionConfig { Action = ButtonActionType.None }
                }
            },
            OutputDevices = new OutputDevicesConfig { Device1 = "Speakers", Device2 = "Headphones" }
        };

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(config, options);
        var deserialized = JsonSerializer.Deserialize<AppConfig>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(ConnectionType.Serial, deserialized.Connection.Type);
        Assert.Equal("COM3", deserialized.Connection.Serial.ComPort);
        Assert.Equal(3, deserialized.Sliders.Count);
        Assert.Equal(SliderMappingType.Unmapped, deserialized.Sliders[2].Type);
        Assert.True(deserialized.Sliders[2].Invert);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, deserialized.Buttons[0].SinglePress.Action);
        Assert.Equal("Headphones", deserialized.OutputDevices.Device2);
    }

    [Fact]
    public void AppConfig_OldJsonWithoutTuningFields_UsesDefaults()
    {
        // JSON predating tuning fields
        var json = """
        { "sliders": [ { "index": 0, "type": "Device" } ],
          "buttons": [ { "index": 0 } ] }
        """;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, options)!;

        Assert.Equal(ResponseCurve.Linear, cfg.Sliders[0].Curve);
        Assert.Equal(0d, cfg.Sliders[0].DeadZonePercent);
        Assert.Equal(0d, cfg.Sliders[0].SmoothingFactor);
        Assert.Equal(500, cfg.Buttons[0].LongPressMs);
        Assert.Equal(300, cfg.Buttons[0].DoublePressWindowMs);
    }

    [Fact]
    public void AppConfig_DefaultConfig_HasFiveSlidersAndThreeButtons()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(5, config.Sliders.Count);
        Assert.Equal(3, config.Buttons.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal(i, config.Sliders[i].Index);
        for (int i = 0; i < 3; i++)
            Assert.Equal(i, config.Buttons[i].Index);
    }
}

public class ConfigManagerLoadSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigManagerLoadSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultConfig()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();

        Assert.Equal(5, manager.Config.Sliders.Count);
        Assert.Equal(3, manager.Config.Buttons.Count);
    }

    [Fact]
    public void Save_ThenLoad_PreservesConfig()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        manager.Config.Connection.Type = ConnectionType.Udp;
        manager.Config.OutputDevices.Device1 = "Speakers";
        manager.Save();

        var manager2 = new ConfigManager(_configPath);
        manager2.Load();

        Assert.Equal(ConnectionType.Udp, manager2.Config.Connection.Type);
        Assert.Equal("Speakers", manager2.Config.OutputDevices.Device1);
    }

    [Fact]
    public void Save_FiresConfigChangedEvent()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        bool eventFired = false;
        manager.ConfigChanged += (_, _) => eventFired = true;

        manager.Save();

        Assert.True(eventFired);
    }

    [Fact]
    public async Task RequestSave_CoalescesMultipleCallsIntoOneWrite()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        int writes = 0;
        manager.ConfigChanged += (_, _) => writes++;

        for (int i = 0; i < 10; i++) manager.RequestSave();
        await Task.Delay(700); // past the ~400ms debounce window

        Assert.Equal(1, writes);
        Assert.True(File.Exists(_configPath));
    }

    [Fact]
    public async Task RequestSave_ConnectionChanged_RaisesConnectionEventOnly()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        bool conn = false, general = false;
        manager.ConnectionConfigChanged += (_, _) => conn = true;
        manager.ConfigChanged += (_, _) => general = true;

        manager.RequestSave(connectionChanged: true);
        await Task.Delay(700);

        Assert.True(conn);
        Assert.False(general);
    }

    [Fact]
    public void Load_CorruptJson_FallsBackToDefaultsInsteadOfThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-corrupt-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ \"connection\": { \"type\": ");   // truncated mid-write

        var cm = new ConfigManager(path);
        var ex = Record.Exception(() => cm.Load());

        Assert.Null(ex);
        Assert.Equal(5, cm.Config.Sliders.Count);
        Assert.Equal(3, cm.Config.Buttons.Count);
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-empty-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "");

        var cm = new ConfigManager(path);
        cm.Load();

        Assert.Equal(5, cm.Config.Sliders.Count);
    }

    [Fact]
    public void CreateDefault_StampsCurrentSchemaVersion()
    {
        Assert.Equal(AppConfig.CurrentSchemaVersion, AppConfig.CreateDefault().SchemaVersion);
    }

    [Fact]
    public void DeviceRefConfig_RoundTripsThroughJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-roundtrip-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        var cm = new ConfigManager(path);
        cm.Load();
        cm.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
        cm.Save();

        var reloaded = new ConfigManager(path);
        reloaded.Load();

        Assert.Equal("dev-1", reloaded.Config.OutputDevices.Device1.Id);
        Assert.Equal("Speakers", reloaded.Config.OutputDevices.Device1.FriendlyName);
    }
}
