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
            OutputDevices = new OutputDevicesConfig
            {
                Device1 = new DeviceRefConfig { FriendlyName = "Speakers" },
                Device2 = new DeviceRefConfig { FriendlyName = "Headphones" }
            }
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
        Assert.Equal("Headphones", deserialized.OutputDevices.Device2.FriendlyName);
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
        manager.Config.OutputDevices.Device1 = new DeviceRefConfig { FriendlyName = "Speakers" };
        manager.Save();

        var manager2 = new ConfigManager(_configPath);
        manager2.Load();

        Assert.Equal(ConnectionType.Udp, manager2.Config.Connection.Type);
        Assert.Equal("Speakers", manager2.Config.OutputDevices.Device1.FriendlyName);
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

    [Fact]
    public void Load_V1ShapedConfig_LoadsLosslessly_AndSchemaVersionIsZero()
    {
        // A realistic schema-v1 config.json: no "schemaVersion" key, and outputDevices'
        // device1/device2 are bare strings (the type that changed under DeviceRefConfig).
        var json = """
        {
            "connection": { "type": "Udp", "udp": { "listenPort": 55555 } },
            "sliders": [
                { "index": 0, "type": "Processes", "processes": [ "spotify.exe" ], "deviceName": "" },
                { "index": 1, "type": "Device", "deviceName": "Speakers" }
            ],
            "buttons": [
                {
                    "index": 0,
                    "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Speakers" },
                    "doublePress": { "action": "ToggleOutputDevice" },
                    "longPress": { "action": "None" }
                }
            ],
            "outputDevices": { "device1": "Speakers", "device2": "Headphones" },
            "debugLogging": true
        }
        """;
        File.WriteAllText(_configPath, json);

        var cm = new ConfigManager(_configPath);
        var ex = Record.Exception(() => cm.Load());

        Assert.Null(ex);

        // A pre-v2 file has no schemaVersion key, so it must deserialize to 0, not
        // CurrentSchemaVersion — that's what lets ConfigMigrator detect it.
        Assert.Equal(0, cm.Config.SchemaVersion);

        // The device string was read losslessly into the id-less v2 shape.
        Assert.Equal("Speakers", cm.Config.OutputDevices.Device1.FriendlyName);
        Assert.Equal("", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("Headphones", cm.Config.OutputDevices.Device2.FriendlyName);

        // Regression guard: everything else in the real config must survive — the old
        // blanket catch used to replace the WHOLE config with CreateDefault() here.
        Assert.Equal(ConnectionType.Udp, cm.Config.Connection.Type);
        Assert.Equal(55555, cm.Config.Connection.Udp.ListenPort);
        Assert.True(cm.Config.DebugLogging);

        // Load() pads sparse lists up to the minimum 5 sliders / 3 buttons (Step 5's defensive
        // behavior) — the two real sliders and one real button from the file must still be the
        // ones at index 0/1/0, unmolested; the padding is additive, not destructive.
        Assert.True(cm.Config.Sliders.Count >= 2);
        Assert.Equal(SliderMappingType.Processes, cm.Config.Sliders[0].Type);
        Assert.Equal(new List<string> { "spotify.exe" }, cm.Config.Sliders[0].Processes);
        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[1].Type);

        Assert.True(cm.Config.Buttons.Count >= 1);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, cm.Config.Buttons[0].SinglePress.Action);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, cm.Config.Buttons[0].DoublePress.Action);
        Assert.Equal(ButtonActionType.None, cm.Config.Buttons[0].LongPress.Action);
    }

    [Fact]
    public void DeviceRefConfig_WrittenAsObject_ReadsBackAsObject()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var config = new OutputDevicesConfig
        {
            Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" }
        };

        var json = JsonSerializer.Serialize(config, options);

        // Written as a v2 object (id + friendlyName), never as a bare string.
        using (var doc = JsonDocument.Parse(json))
        {
            var device1 = doc.RootElement.GetProperty("device1");
            Assert.Equal(JsonValueKind.Object, device1.ValueKind);
            Assert.Equal("dev-1", device1.GetProperty("id").GetString());
            Assert.Equal("Speakers", device1.GetProperty("friendlyName").GetString());
        }

        var roundTripped = JsonSerializer.Deserialize<OutputDevicesConfig>(json, options)!;
        Assert.Equal("dev-1", roundTripped.Device1.Id);
        Assert.Equal("Speakers", roundTripped.Device1.FriendlyName);
    }
}
