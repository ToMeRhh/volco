using System.Text.Json;
using Volco.Core.Audio;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

public class ConfigMigratorTests
{
    private static readonly IReadOnlyList<AudioDevice> Devices = new List<AudioDevice>
    {
        new("dev-1", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true),
        new("dev-2", "Headphones (USB Audio Device)",            AudioFlow.Render, false),
    };

    private const string V1Json = """
    {
      "connection": { "type": "Udp", "udp": { "listenPort": 12345 } },
      "sliders": [
        { "index": 0, "type": "Device",    "deviceName": "Headphones", "processes": [] },
        { "index": 1, "type": "Processes", "deviceName": "",           "processes": ["spotify.exe"] }
      ],
      "buttons": [
        { "index": 0, "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Speakers" },
                      "doublePress": { "action": "None" }, "longPress": { "action": "None" } }
      ],
      "outputDevices": { "device1": "Speakers", "device2": "Headphones" },
      "debugLogging": false
    }
    """;

    private static string WriteTempConfig(string json)
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
        Assert.Equal("dev-1", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("dev-2", cm.Config.OutputDevices.Device2.Id);
        Assert.Equal("dev-1", cm.Config.Buttons[0].SinglePress.Device!.Id);
    }

    [Fact]
    public void MigrateFile_UnresolvableName_KeepsNameAndLeavesIdEmpty()
    {
        var json = V1Json.Replace("\"device1\": \"Speakers\"", "\"device1\": \"Old Soundcard\"");
        var path = WriteTempConfig(json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal("", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("Old Soundcard", cm.Config.OutputDevices.Device1.FriendlyName);

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
        Assert.Equal("dev-1", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("dev-2", cm.Config.OutputDevices.Device2.Id);
        Assert.Equal("dev-1", cm.Config.Buttons[0].SinglePress.Device!.Id);

        // Migration must not be a data-loss step: non-device settings survive intact.
        Assert.Equal(ConnectionType.Serial, cm.Config.Connection.Type);
        Assert.Equal("COM7", cm.Config.Connection.Serial.ComPort);
        Assert.Equal(9600, cm.Config.Connection.Serial.BaudRate);

        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[0].Type);
        Assert.True(cm.Config.Sliders[0].Invert);
        Assert.Equal(ResponseCurve.Logarithmic, cm.Config.Sliders[0].Curve);
        Assert.Equal(SliderMappingType.Processes, cm.Config.Sliders[1].Type);
        Assert.Equal(new List<string> { "spotify.exe", "chrome.exe" }, cm.Config.Sliders[1].Processes);
        Assert.Equal(SliderMappingType.Device, cm.Config.Sliders[2].Type);

        Assert.Equal(ButtonActionType.MuteSpecificDevice, cm.Config.Buttons[0].SinglePress.Action);
        Assert.Equal(ButtonActionType.ToggleOutputDevice, cm.Config.Buttons[0].DoublePress.Action);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, cm.Config.Buttons[1].SinglePress.Action);
    }
}
