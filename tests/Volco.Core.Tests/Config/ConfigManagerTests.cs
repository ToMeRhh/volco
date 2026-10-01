using System.Text.Json;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

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
                    Actions =
                    {
                        [AppConfig.DefaultPresetId] = new ButtonBindingConfig
                        {
                            SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput },
                            DoublePress = new ButtonActionConfig
                            {
                                Action = ButtonActionType.ToggleOutputDevice,
                                Device = new DeviceRefConfig { FriendlyName = "Speakers" },
                                DeviceB = new DeviceRefConfig { FriendlyName = "Headphones" },
                            },
                        },
                        ["gaming"] = new ButtonBindingConfig
                        {
                            LongPress = new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "default" },
                        },
                    },
                }
            },
            Presets =
            {
                new PresetConfig { Id = AppConfig.DefaultPresetId, Name = "Default" },
                new PresetConfig { Id = "gaming", Name = "Gaming" },
            },
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
        Assert.Equal(ButtonActionType.MuteCurrentOutput, deserialized.Buttons[0].Default().SinglePress.Action);
        Assert.Equal("Headphones", deserialized.Buttons[0].Default().DoublePress.DeviceB!.FriendlyName);
        Assert.Equal("default", deserialized.Buttons[0].BindingFor("gaming").LongPress.PresetId);
        Assert.Equal(new[] { "Default", "Gaming" }, deserialized.Presets.Select(p => p.Name));
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

    [Fact]
    public void AppConfig_DefaultConfig_IsNotSetUp_AndHasOnePreset()
    {
        var config = AppConfig.CreateDefault();

        Assert.All(config.Sliders, s => Assert.Equal(SliderMappingType.None, s.Type));
        Assert.All(config.Buttons, b => Assert.Empty(b.Actions));
        var preset = Assert.Single(config.Presets);
        Assert.Equal(AppConfig.DefaultPresetId, preset.Id);
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
        manager.Config.Buttons[1].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.SetOutputDevice,
            Device = new DeviceRefConfig { FriendlyName = "Speakers" },
        };
        manager.Save();

        var manager2 = new ConfigManager(_configPath);
        manager2.Load();

        Assert.Equal(ConnectionType.Udp, manager2.Config.Connection.Type);
        Assert.Equal("Speakers", manager2.Config.Buttons[1].Default().SinglePress.Device!.FriendlyName);
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
        cm.Config.Sliders[0].Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
        cm.Save();

        var reloaded = new ConfigManager(path);
        reloaded.Load();

        Assert.Equal("dev-1", reloaded.Config.Sliders[0].Device!.Id);
        Assert.Equal("Speakers", reloaded.Config.Sliders[0].Device!.FriendlyName);
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

        // The global toggle pair's bare strings were read losslessly into the id-less shape, on
        // the toggle action itself (schema v3 keeps the pair on the action).
        var toggle = cm.Config.Buttons[0].Default().DoublePress;
        Assert.Equal("Speakers", toggle.Device!.FriendlyName);
        Assert.Equal("", toggle.Device.Id);
        Assert.Equal("Headphones", toggle.DeviceB!.FriendlyName);

        // Regression guard: everything else in the real config must survive — the old
        // blanket catch used to replace the WHOLE config with CreateDefault() here.
        Assert.Equal(ConnectionType.Udp, cm.Config.Connection.Type);
        Assert.Equal(55555, cm.Config.Connection.Udp.ListenPort);
        Assert.True(cm.Config.DebugLogging);

        // Schema v3: the number of controls is the user's, so a short list is not padded.
        Assert.Equal(2, cm.Config.Sliders.Count);
        Assert.Equal(SliderMappingType.Processes, cm.Config.Sliders[0].Type);
        Assert.Equal(new List<string> { "spotify.exe" }, cm.Config.Sliders[0].Processes);
        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[1].Type);

        Assert.Single(cm.Config.Buttons);
        Assert.Equal(ButtonActionType.MuteSpecificDevice, cm.Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, cm.Config.Buttons[0].Default().DoublePress.Action);
        Assert.Equal(ButtonActionType.None, cm.Config.Buttons[0].Default().LongPress.Action);
    }

    [Fact]
    public void DeviceRefConfig_WrittenAsObject_ReadsBackAsObject()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var config = new ButtonActionConfig
        {
            Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" }
        };

        var json = JsonSerializer.Serialize(config, options);

        // Written as a v2 object (id + friendlyName), never as a bare string.
        using (var doc = JsonDocument.Parse(json))
        {
            var device = doc.RootElement.GetProperty("device");
            Assert.Equal(JsonValueKind.Object, device.ValueKind);
            Assert.Equal("dev-1", device.GetProperty("id").GetString());
            Assert.Equal("Speakers", device.GetProperty("friendlyName").GetString());
        }

        var roundTripped = JsonSerializer.Deserialize<ButtonActionConfig>(json, options)!;
        Assert.Equal("dev-1", roundTripped.Device!.Id);
        Assert.Equal("Speakers", roundTripped.Device.FriendlyName);
    }

    // ── Schema v3 ────────────────────────────────────────────────────────────

    /// <summary>A v2 config.json as the v2 app wrote it (camelCase, device objects, global toggle pair).</summary>
    internal const string V2Json = """
    {
      "schemaVersion": 2,
      "connection": { "type": "Serial", "serial": { "comPort": "COM3", "baudRate": 115200 } },
      "sliders": [
        { "index": 0, "type": "Device", "processes": [], "device": null, "invert": false, "curve": "Linear", "deadZonePercent": 0, "smoothingFactor": 0 },
        { "index": 1, "type": "Processes", "processes": [ "spotify.exe" ], "device": null, "invert": false, "curve": "Logarithmic", "deadZonePercent": 0.02, "smoothingFactor": 0.2 },
        { "index": 2, "type": "Unmapped", "processes": [], "device": null },
        { "index": 3, "type": "Device", "processes": [], "device": { "id": "dev-2", "friendlyName": "Headphones" } },
        { "index": 4, "type": "Processes", "processes": [ "discord.exe" ], "device": null }
      ],
      "buttons": [
        { "index": 0, "singlePress": { "action": "MuteCurrentInput", "device": null },
                      "doublePress": { "action": "None", "device": null },
                      "longPress": { "action": "None", "device": null },
                      "longPressMs": 600, "doublePressWindowMs": 250 },
        { "index": 1, "singlePress": { "action": "MuteSpecificDevice", "device": { "id": "dev-1", "friendlyName": "Speakers" } },
                      "doublePress": { "action": "None", "device": null },
                      "longPress": { "action": "None", "device": null },
                      "longPressMs": 500, "doublePressWindowMs": 300 },
        { "index": 2, "singlePress": { "action": "ToggleOutputDevice", "device": null },
                      "doublePress": { "action": "None", "device": null },
                      "longPress": { "action": "SetOutputDevice", "device": { "id": "dev-2", "friendlyName": "Headphones" } },
                      "longPressMs": 500, "doublePressWindowMs": 300 }
      ],
      "outputDevices": { "device1": { "id": "dev-1", "friendlyName": "Speakers" },
                         "device2": { "id": "dev-2", "friendlyName": "Headphones" } },
      "updates": { "gitHubToken": "ghp_x", "useCiArtifact": true },
      "debugLogging": true
    }
    """;

    [Fact]
    public void Load_V2Config_MigratesToV3_WithoutLosingAnything()
    {
        File.WriteAllText(_configPath, V2Json);

        var cm = new ConfigManager(_configPath);
        cm.Load();
        var c = cm.Config;

        Assert.Equal(3, c.SchemaVersion);
        Assert.Equal(ConnectionType.Serial, c.Connection.Type);
        Assert.Equal("COM3", c.Connection.Serial.ComPort);
        Assert.True(c.DebugLogging);
        Assert.Equal("ghp_x", c.Updates.GitHubToken);
        Assert.True(c.Updates.UseCiArtifact);

        Assert.Equal(5, c.Sliders.Count);
        Assert.Equal(SliderMappingType.Unmapped, c.Sliders[2].Type);
        Assert.Equal("dev-2", c.Sliders[3].Device!.Id);
        Assert.Equal(ResponseCurve.Logarithmic, c.Sliders[1].Curve);
        Assert.Equal(0.2, c.Sliders[1].SmoothingFactor);

        var preset = Assert.Single(c.Presets);
        Assert.Equal(AppConfig.DefaultPresetId, preset.Id);

        Assert.Equal(3, c.Buttons.Count);
        Assert.Equal(600, c.Buttons[0].LongPressMs);
        Assert.Equal(250, c.Buttons[0].DoublePressWindowMs);
        Assert.Equal(ButtonActionType.MuteCurrentInput, c.Buttons[0].Default().SinglePress.Action);
        Assert.Equal("dev-1", c.Buttons[1].Default().SinglePress.Device!.Id);

        // The global toggle pair moved onto the toggle action.
        var toggle = c.Buttons[2].Default().SinglePress;
        Assert.Equal(ButtonActionType.ToggleOutputDevice, toggle.Action);
        Assert.Equal("dev-1", toggle.Device!.Id);
        Assert.Equal("dev-2", toggle.DeviceB!.Id);
        Assert.Equal("dev-2", c.Buttons[2].Default().LongPress.Device!.Id);
    }

    [Fact]
    public void Load_V2Config_WritesV3ToDisk_AndKeepsAV2Backup()
    {
        File.WriteAllText(_configPath, V2Json);

        new ConfigManager(_configPath).Load();

        var onDisk = File.ReadAllText(_configPath);
        Assert.Contains("\"schemaVersion\": 3", onDisk);
        Assert.DoesNotContain("outputDevices", onDisk);
        Assert.Contains("\"actions\"", onDisk);

        var backup = Path.Combine(_tempDir, "config.v2.bak");
        Assert.Equal(V2Json, File.ReadAllText(backup));

        // Loading the migrated file again is a no-op.
        var again = new ConfigManager(_configPath);
        again.Load();
        Assert.Equal("dev-2", again.Config.Buttons[2].Default().SinglePress.DeviceB!.Id);
        Assert.Equal(V2Json, File.ReadAllText(backup));
    }

    [Fact]
    public void Save_RewritesIndexToListPosition()
    {
        var cm = new ConfigManager(_configPath);
        cm.Load();
        var moved = cm.Config.Sliders[4];
        cm.Config.Sliders.RemoveAt(4);
        cm.Config.Sliders.Insert(0, moved);
        cm.Save();

        var reloaded = new ConfigManager(_configPath);
        reloaded.Load();
        for (int i = 0; i < reloaded.Config.Sliders.Count; i++)
            Assert.Equal(i, reloaded.Config.Sliders[i].Index);
        Assert.Contains("\"index\": 0", File.ReadAllText(_configPath));
    }

    [Fact]
    public void Load_V3ConfigWithNoPresets_GetsTheDefaultPreset()
    {
        File.WriteAllText(_configPath, """{ "schemaVersion": 3, "sliders": [], "buttons": [ { "index": 0 } ], "presets": [] }""");

        var cm = new ConfigManager(_configPath);
        cm.Load();

        Assert.Single(cm.Config.Presets);
        Assert.Empty(cm.Config.Sliders);
        Assert.NotNull(cm.Config.Buttons[0].Actions);
    }
}
