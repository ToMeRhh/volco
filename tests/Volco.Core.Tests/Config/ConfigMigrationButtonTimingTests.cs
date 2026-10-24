using System.Text.Json.Nodes;
using Volco.Core.Config;
using Xunit;

namespace Volco.Core.Tests.Config;

/// <summary>Schema v8: one hold and double-press timing for the controller (#109); sliders drop
/// invert, curve and dead zone.</summary>
public class ConfigMigrationButtonTimingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigMigrationButtonTimingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string V7Json(string buttons) => $$"""
    { "schemaVersion": 7, "connection": { "type": "Serial", "serial": { "comPort": "COM7" } },
      "sliders": [ { "index": 0, "type": "Device", "invert": true, "curve": "Logarithmic", "deadZonePercent": 3 },
                   { "index": 1, "type": "Device", "invert": false } ],
      "buttons": {{buttons}},
      "modes": [ { "id": "default", "name": "Default" } ] }
    """;

    private (AppConfig Config, string OnDisk, string Original) Load(string json)
    {
        File.WriteAllText(_configPath, json);
        var cm = new ConfigManager(_configPath);
        cm.Load();
        return (cm.Config, File.ReadAllText(_configPath), json);
    }

    [Fact]
    public void Load_V7_MovesTheFirstButtonsTimingToButtonTiming()
    {
        var (c, onDisk, original) = Load(V7Json(
            """[ { "index": 0, "longPressMs": 700, "doublePressWindowMs": 250 }, { "index": 1, "longPressMs": 900 } ]"""));

        Assert.Equal(700, c.ButtonTiming.LongPressMs);
        Assert.Equal(250, c.ButtonTiming.DoublePressWindowMs);
        Assert.Equal(AppConfig.CurrentSchemaVersion, c.SchemaVersion);
        var root = JsonNode.Parse(onDisk)!;
        foreach (var b in root["buttons"]!.AsArray())
        {
            Assert.False(b!.AsObject().ContainsKey("longPressMs"));
            Assert.False(b.AsObject().ContainsKey("doublePressWindowMs"));
        }
        Assert.Equal(700, root["buttonTiming"]!["longPressMs"]!.GetValue<int>());
        Assert.DoesNotContain("invert", onDisk);
        Assert.DoesNotContain("curve", onDisk);
        Assert.DoesNotContain("deadZonePercent", onDisk);
        Assert.Contains($"\"schemaVersion\": {AppConfig.CurrentSchemaVersion}", onDisk);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_tempDir, "config.v7.bak")));
    }

    [Fact]
    public void Load_V7_NoButtons_DefaultTiming()
    {
        var (c, _, _) = Load(V7Json("[]"));

        Assert.Equal(500, c.ButtonTiming.LongPressMs);
        Assert.Equal(300, c.ButtonTiming.DoublePressWindowMs);
    }

    [Fact]
    public void Load_V7_FirstButtonWithoutTiming_DefaultTiming()
    {
        var (c, _, _) = Load(V7Json(
            """[ { "index": 0 }, { "index": 1, "longPressMs": 900, "doublePressWindowMs": 400 } ]"""));

        Assert.Equal(500, c.ButtonTiming.LongPressMs);
        Assert.Equal(300, c.ButtonTiming.DoublePressWindowMs);
    }

    [Fact]
    public void ButtonTiming_RoundTripsThroughSave()
    {
        var cm = new ConfigManager(_configPath);
        cm.Load();
        cm.Config.ButtonTiming.LongPressMs = 800;
        cm.Config.ButtonTiming.DoublePressWindowMs = 150;
        cm.Save();

        var again = new ConfigManager(_configPath);
        again.Load();
        Assert.Equal(800, again.Config.ButtonTiming.LongPressMs);
        Assert.Equal(150, again.Config.ButtonTiming.DoublePressWindowMs);
        Assert.Contains("\"buttonTiming\"", File.ReadAllText(_configPath));
    }
}
