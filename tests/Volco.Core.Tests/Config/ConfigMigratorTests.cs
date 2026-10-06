using System.Text.Json;
using Volco.Core.Audio;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

public class ConfigMigratorTests
{
    internal static readonly IReadOnlyList<AudioDevice> Devices = new List<AudioDevice>
    {
        new("dev-1", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true),
        new("dev-2", "Headphones (USB Audio Device)",            AudioFlow.Render, false),
    };

    internal const string V1Json = """
    {
      "connection": { "type": "Udp", "udp": { "listenPort": 12345 } },
      "sliders": [
        { "index": 0, "type": "Device",    "deviceName": "Headphones", "processes": [] },
        { "index": 1, "type": "Processes", "deviceName": "",           "processes": ["spotify.exe"] }
      ],
      "buttons": [
        { "index": 0, "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Speakers" },
                      "doublePress": { "action": "ToggleOutputDevice" }, "longPress": { "action": "None" } }
      ],
      "outputDevices": { "device1": "Speakers", "device2": "Headphones" },
      "debugLogging": false
    }
    """;

    internal static string WriteTempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-migrate-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void NeedsMigration_TrueWhenSchemaVersionAbsent()
        => Assert.True(ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = 0 }));

    [Fact]
    public void NeedsMigration_FalseWhenAlreadyCurrent()
        => Assert.False(ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion }));

    [Fact]
    public void ResolveName_PrefersExactFriendlyNameMatch()
    {
        var d = ConfigMigrator.ResolveName("Headphones (USB Audio Device)", Devices);
        Assert.Equal("dev-2", d!.Id);
    }

    [Fact]
    public void ResolveName_FallsBackToSubstringMatch()
    {
        // v1 stored whatever substring the user typed.
        var d = ConfigMigrator.ResolveName("Headphones", Devices);
        Assert.Equal("dev-2", d!.Id);
    }

    [Fact]
    public void ResolveName_UnknownDevice_ReturnsNull()
        => Assert.Null(ConfigMigrator.ResolveName("Nonexistent Soundcard", Devices));

    [Fact]
    public void MigrateFile_PopulatesIdsFromV1Names()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
        Assert.Null(cm.Config.Sliders[1].Device);                 // Processes slider, no device
        // The v1 global toggle pair lands on the toggle action (schema v3).
        Assert.Equal("dev-1", cm.Config.Buttons[0].Default().DoublePress.Device!.Id);
        Assert.Equal("dev-2", cm.Config.Buttons[0].Default().DoublePress.DeviceB!.Id);
        Assert.Equal("dev-1", cm.Config.Buttons[0].Default().SinglePress.Device!.Id);
    }

    [Fact]
    public void MigrateFile_UnresolvableName_KeepsNameAndLeavesIdEmpty()
    {
        var json = V1Json.Replace("\"device1\": \"Speakers\"", "\"device1\": \"Old Soundcard\"");
        var path = WriteTempConfig(json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        var toggle = cm.Config.Buttons[0].Default().DoublePress;
        Assert.Equal("", toggle.Device!.Id);
        Assert.Equal("Old Soundcard", toggle.Device.FriendlyName);

        // These name/id assertions alone would hold even if MigrateFile were a no-op, because
        // DeviceRefConfigJsonConverter already produces this exact shape at Load() time. Assert
        // migration actually ran.
        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
    }

    [Fact]
    public void MigrateFile_WritesBackupOfTheOriginal()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        var backup = Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak");
        Assert.True(File.Exists(backup));
        Assert.Contains("\"deviceName\": \"Headphones\"", File.ReadAllText(backup));
    }

    [Fact]
    public void MigrateFile_IsIdempotent_AndDoesNotOverwriteAnExistingBackup()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak");
        var firstBackup = File.ReadAllText(backup);

        // Second run: config is now v2, so nothing should happen.
        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal(firstBackup, File.ReadAllText(backup));
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
    }

    /// <summary>
    /// Reaches the `!File.Exists(backup)` guard for real: unlike
    /// MigrateFile_IsIdempotent_AndDoesNotOverwriteAnExistingBackup (which reuses an
    /// already-migrated config, so the second call short-circuits at the top-level
    /// NeedsMigration check and never reaches the backup-write code at all), this simulates a
    /// crash-interrupted earlier run by pre-seeding config.v1.bak with distinct content, then
    /// performs a genuine migration (fresh SchemaVersion == 0 from a real Load()). If the
    /// `!File.Exists(backup)` condition were removed or inverted, this test would fail because
    /// the marker content would be overwritten with the current raw config.
    /// </summary>
    [Fact]
    public void MigrateFile_ExistingBackupFromInterruptedRun_IsNotOverwritten()
    {
        var path = WriteTempConfig(V1Json);
        var dir = Path.GetDirectoryName(path)!;
        var backup = Path.Combine(dir, "config.v1.bak");
        const string PreservedMarker = "PRISTINE-V1-BACKUP-FROM-EARLIER-INTERRUPTED-RUN";
        File.WriteAllText(backup, PreservedMarker);

        var cm = new ConfigManager(path);
        cm.Load(); // fresh load: SchemaVersion == 0, NeedsMigration is genuinely true

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        // The pre-existing backup must survive untouched...
        Assert.Equal(PreservedMarker, File.ReadAllText(backup));

        // ...even though migration itself still ran and did its job.
        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
    }

    // ── Fix C: HealDeviceIds ─────────────────────────────────────────────────

    [Fact]
    public void HealDeviceIds_FillsBlankIdsForTogglePair_InEveryPreset_ByExactName()
    {
        var config = AppConfig.CreateDefault();
        config.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        var toggle = config.Buttons[0].GetOrAddBinding("gaming").LongPress = Toggle(
            new DeviceRefConfig { Id = "", FriendlyName = "Speakers (Realtek High Definition Audio)" },
            new DeviceRefConfig { Id = "", FriendlyName = "Headphones (USB Audio Device)" });

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.True(changed);
        Assert.Equal("dev-1", toggle.Device!.Id);
        Assert.Equal("dev-2", toggle.DeviceB!.Id);
    }

    private static ButtonActionConfig Toggle(DeviceRefConfig a, DeviceRefConfig? b = null)
        => new() { Action = ButtonActionType.ToggleOutputDevice, Device = a, DeviceB = b };

    [Fact]
    public void HealDeviceIds_FillsBlankIdForSliderDevice_ByExactName()
    {
        var config = AppConfig.CreateDefault();
        config.Sliders[0].Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers (Realtek High Definition Audio)" };

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.True(changed);
        Assert.Equal("dev-1", config.Sliders[0].Device!.Id);
    }

    [Fact]
    public void HealDeviceIds_FillsBlankIdForButtonAction_ByExactName()
    {
        var config = AppConfig.CreateDefault();
        config.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Headphones (USB Audio Device)" },
        };
        config.Buttons[0].Default().DoublePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MuteSpecificDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers (Realtek High Definition Audio)" },
        };
        config.Buttons[0].Default().LongPress = new ButtonActionConfig
        {
            Action = ButtonActionType.SetOutputDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Headphones (USB Audio Device)" },
        };

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.True(changed);
        Assert.Equal("dev-2", config.Buttons[0].Default().SinglePress.Device!.Id);
        Assert.Equal("dev-1", config.Buttons[0].Default().DoublePress.Device!.Id);
        Assert.Equal("dev-2", config.Buttons[0].Default().LongPress.Device!.Id);
    }

    [Fact]
    public void HealDeviceIds_NeverTouchesANonEmptyId_EvenIfNameMatchesADifferentDevice()
    {
        var config = AppConfig.CreateDefault();
        // Id already points at dev-1, but the name (if it were consulted) matches dev-2.
        var a = (config.Buttons[0].Default().SinglePress = Toggle(new DeviceRefConfig { Id = "dev-1", FriendlyName = "Headphones (USB Audio Device)" })).Device!;

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.False(changed);
        Assert.Equal("dev-1", a.Id);
        Assert.Equal("Headphones (USB Audio Device)", a.FriendlyName);
    }

    [Fact]
    public void HealDeviceIds_IgnoresPartialNameMatches()
    {
        var config = AppConfig.CreateDefault();
        // "Headphones" is a substring of "Headphones (USB Audio Device)" - ResolveName's v1
        // fallback would match this, but HealDeviceIds must not.
        var a = (config.Buttons[0].Default().SinglePress = Toggle(new DeviceRefConfig { Id = "", FriendlyName = "Headphones" })).Device!;

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.False(changed);
        Assert.Equal("", a.Id);
        Assert.Equal("Headphones", a.FriendlyName);
    }

    [Fact]
    public void HealDeviceIds_AmbiguousFriendlyName_LeavesReferenceUnresolved()
    {
        // Two present devices share the same friendly name - healing would have to guess which
        // one was meant, so neither should be picked.
        var ambiguousDevices = new List<AudioDevice>
        {
            new("dev-1", "Speakers", AudioFlow.Render, true),
            new("dev-3", "Speakers", AudioFlow.Render, false),
        };
        var config = AppConfig.CreateDefault();
        var a = (config.Buttons[0].Default().SinglePress = Toggle(new DeviceRefConfig { Id = "", FriendlyName = "Speakers" })).Device!;

        var changed = ConfigMigrator.HealDeviceIds(config, ambiguousDevices);

        Assert.False(changed);
        Assert.Equal("", a.Id);
        Assert.Equal("Speakers", a.FriendlyName);
    }

    [Fact]
    public void HealDeviceIds_MatchesCaseInsensitively_AndNormalisesFriendlyNameCasing()
    {
        var config = AppConfig.CreateDefault();
        // Config carries the name in a different casing than the device reports.
        var a = (config.Buttons[0].Default().SinglePress = Toggle(new DeviceRefConfig { Id = "", FriendlyName = "speakers (realtek high definition audio)" })).Device!;

        var changed = ConfigMigrator.HealDeviceIds(config, Devices);

        Assert.True(changed);
        Assert.Equal("dev-1", a.Id);
        Assert.Equal("Speakers (Realtek High Definition Audio)", a.FriendlyName);
    }

    [Fact]
    public void HealDeviceIds_ReturnsFalse_WhenNothingChanges()
    {
        // CreateDefault(): no button actions, and sliders default to Device=null.
        var config = AppConfig.CreateDefault();

        Assert.False(ConfigMigrator.HealDeviceIds(config, Devices));
    }

    [Fact]
    public void MigrateFile_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"volco-missing-{Guid.NewGuid()}", "config.json");
        var config = AppConfig.CreateDefault();
        config.SchemaVersion = 0;

        Assert.Null(Record.Exception(() => ConfigMigrator.MigrateFile(path, config, Devices)));
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }

    /// <summary>
    /// End-to-end: a realistic v1 config.json loaded through the real ConfigManager (not a
    /// hand-built AppConfig) must be detected as needing migration, and MigrateFile must both
    /// resolve device ids AND leave every non-device setting untouched.
    /// </summary>
    [Fact]
    public void EndToEnd_RealV1File_LoadedThroughConfigManager_IsDetectedAndMigratedWithoutDataLoss()
    {
        const string realisticV1Json = """
        {
          "connection": { "type": "Serial", "serial": { "comPort": "COM7", "baudRate": 9600 } },
          "sliders": [
            { "index": 0, "type": "Device", "deviceName": "Speakers", "processes": [], "invert": true, "curve": "Logarithmic" },
            { "index": 1, "type": "Processes", "deviceName": "", "processes": ["spotify.exe", "chrome.exe"] },
            { "index": 2, "type": "Device", "deviceName": "Headphones", "processes": [] },
            { "index": 3, "type": "Unmapped", "deviceName": "", "processes": [] },
            { "index": 4, "type": "Unmapped", "deviceName": "", "processes": [] }
          ],
          "buttons": [
            { "index": 0, "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Speakers" },
                          "doublePress": { "action": "ToggleOutputDevice" },
                          "longPress": { "action": "None" } },
            { "index": 1, "singlePress": { "action": "MuteCurrentOutput" },
                          "doublePress": { "action": "None" },
                          "longPress": { "action": "None" } },
            { "index": 2, "singlePress": { "action": "None" },
                          "doublePress": { "action": "None" },
                          "longPress": { "action": "None" } }
          ],
          "outputDevices": { "device1": "Speakers", "device2": "Headphones" },
          "debugLogging": false
        }
        """;

        var path = WriteTempConfig(realisticV1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        // The bug this guards against: SchemaVersion used to default to CurrentSchemaVersion,
        // so a real v1 file (no "schemaVersion" key) was never recognized as needing migration.
        Assert.True(ConfigMigrator.NeedsMigration(cm.Config));

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        // Device ids got resolved.
        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
        Assert.Equal("dev-1", cm.Config.Sliders[0].Device!.Id);
        Assert.Null(cm.Config.Sliders[1].Device);
        Assert.Equal("dev-2", cm.Config.Sliders[2].Device!.Id);
        Assert.Equal("dev-1", cm.Config.Buttons[0].Default().DoublePress.Device!.Id);   // toggle A
        Assert.Equal("dev-2", cm.Config.Buttons[0].Default().DoublePress.DeviceB!.Id);  // toggle B
        Assert.Equal("dev-1", cm.Config.Buttons[0].Default().SinglePress.Device!.Id);

        // Migration must not be a data-loss step: non-device settings survive intact.
        Assert.Equal(ConnectionType.Serial, cm.Config.Connection.Type);
        Assert.Equal("COM7", cm.Config.Connection.Serial.ComPort);

        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[0].Type);
        Assert.True(cm.Config.Sliders[0].Invert);
        Assert.Equal(ResponseCurve.Logarithmic, cm.Config.Sliders[0].Curve);
        Assert.Equal(SliderMappingType.Processes, cm.Config.Sliders[1].Type);
        Assert.Equal(new List<string> { "spotify.exe", "chrome.exe" }, cm.Config.Sliders[1].Processes);
        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[2].Type);

        Assert.Equal(ButtonActionType.MuteSpecificDevice, cm.Config.Buttons[0].Default().SinglePress.Action);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, cm.Config.Buttons[0].Default().DoublePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, cm.Config.Buttons[1].Default().SinglePress.Action);
    }

    // ── v2 → v3 shape upgrade ────────────────────────────────────────────────

    [Fact]
    public void UpgradeShapeToV3_IsIdempotent()
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(ConfigManagerLoadSaveTests.V2Json)!;
        ConfigMigrator.UpgradeShapeToV3(root);
        var once = root.ToJsonString();
        ConfigMigrator.UpgradeShapeToV3(root);

        Assert.Equal(once, root.ToJsonString());
    }

    [Fact]
    public void UpgradeShapeToV3_ToggleWithItsOwnDevice_IsNotOverwrittenByTheGlobalPair()
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        { "buttons": [ { "singlePress": { "action": "ToggleOutputDevice", "device": { "id": "mine", "friendlyName": "Mine" } } } ],
          "outputDevices": { "device1": { "id": "g1", "friendlyName": "G1" }, "device2": { "id": "g2", "friendlyName": "G2" } } }
        """)!;

        ConfigMigrator.UpgradeShapeToV3(root);

        var action = root["buttons"]![0]!["actions"]!["default"]!["singlePress"]!;
        Assert.Equal("mine", (string?)action["device"]!["id"]);
        Assert.Equal("g2", (string?)action["deviceB"]!["id"]);
        Assert.Null(root["outputDevices"]);
    }

    [Fact]
    public void EndToEnd_V1File_EndsAsV3_WithThePairOnTheToggleAction()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();
        Assert.True(ConfigMigrator.NeedsMigration(cm.Config));   // still v1 until names are resolved

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);
        cm.Save();

        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(AppConfig.CurrentSchemaVersion, reloaded.Config.SchemaVersion);
        Assert.Equal("dev-2", reloaded.Config.Buttons[0].Default().DoublePress.DeviceB!.Id);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "config.v2.bak")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void NeedsMigration_OnlyForV1Files(int version, bool expected)
        => Assert.Equal(expected, ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = version }));
}
