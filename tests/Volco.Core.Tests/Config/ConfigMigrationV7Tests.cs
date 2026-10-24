using System.Text.Json.Nodes;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Input.Actions;
using Xunit;

namespace Volco.Core.Tests.Config;

/// <summary>Schema v7: button presets become modes (Controls page spec, delivery step 7). Later
/// schemas stamp over it, so these assert the current version.</summary>
public class ConfigMigrationV7Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigMigrationV7Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>A v6 config.json as the v6 app wrote it: two presets, a Go to preset to the
    /// second (or to <paramref name="goToId"/>) and a Next preset.</summary>
    private static string V6Json(string goToId = "preset-2") => $$"""
    {
      "schemaVersion": 6,
      "connection": { "type": "Serial", "serial": { "comPort": "COM7" } },
      "sliders": [],
      "buttons": [
        { "index": 0, "actions": { "default": {
            "singlePress": { "action": "GoToPreset", "device": null, "deviceB": null, "presetId": "{{goToId}}", "mediaApp": null, "command": null, "whileRunning": "Ignore" },
            "doublePress": { "action": "None", "presetId": null },
            "longPress": { "action": "NextPreset", "presetId": null } } },
          "longPressMs": 500, "doublePressWindowMs": 300 }
      ],
      "presets": [ { "id": "default", "name": "Default" }, { "id": "preset-2", "name": "Gaming" } ],
      "devices": [],
      "debugLogging": false
    }
    """;

    private ConfigManager Load(string json)
    {
        File.WriteAllText(_configPath, json);
        var cm = new ConfigManager(_configPath);
        cm.Load();
        return cm;
    }

    [Fact]
    public void V6_PresetsBecomeModes_ActionsRenamed_BakWritten()
    {
        var v6 = V6Json();
        var c = Load(v6).Config;

        Assert.Equal(AppConfig.CurrentSchemaVersion, c.SchemaVersion);
        Assert.Equal(new[] { "default", "preset-2" }, c.Modes.Select(m => m.Id));
        Assert.Equal(new[] { "Default", "Gaming" }, c.Modes.Select(m => m.Name));
        var binding = c.Buttons[0].BindingFor("default");
        Assert.Equal(ButtonActionType.GoToMode, binding.SinglePress.Action);
        Assert.Equal("preset-2", binding.SinglePress.ModeId);
        Assert.Equal(ButtonActionType.NextMode, binding.LongPress.Action);

        Assert.Equal(v6, File.ReadAllText(Path.Combine(_tempDir, "config.v6.bak")));
        var saved = File.ReadAllText(_configPath);
        Assert.Contains("\"modes\"", saved);
        Assert.Contains("\"modeId\"", saved);
        Assert.Contains("\"GoToMode\"", saved);
        Assert.Contains($"\"schemaVersion\": {AppConfig.CurrentSchemaVersion}", saved);
        Assert.DoesNotContain("\"presets\"", saved);
        Assert.DoesNotContain("presetId", saved);
        Assert.DoesNotContain("Preset\"", saved);
    }

    [Fact]
    public void V6_GoToDeletedPreset_KeepsDanglingIdAndValidatorFlagsIt()
    {
        var c = Load(V6Json(goToId: "gone")).Config;

        var action = c.Buttons[0].BindingFor("default").SinglePress;
        Assert.Equal(ButtonActionType.GoToMode, action.Action);
        Assert.Equal("gone", action.ModeId);
        var problems = ActionValidator.Validate(action, ActionValidationContext.Structural(c));
        Assert.Equal("Choose a mode.", problems[ActionValidator.Mode]);
    }

    [Fact]
    public void V2_LoadsToModes()
    {
        var c = Load(ConfigManagerLoadSaveTests.V2Json).Config;

        Assert.Equal(AppConfig.CurrentSchemaVersion, c.SchemaVersion);
        var mode = Assert.Single(c.Modes);
        Assert.Equal(AppConfig.DefaultModeId, mode.Id);
        Assert.Equal(ButtonActionType.MuteCurrentInput, c.Buttons[0].BindingFor(AppConfig.DefaultModeId).SinglePress.Action);
        Assert.True(File.Exists(Path.Combine(_tempDir, "config.v2.bak")));
        var saved = File.ReadAllText(_configPath);
        Assert.Contains("\"modes\"", saved);
        Assert.DoesNotContain("\"presets\"", saved);
    }

    [Fact]
    public void Current_LoadsUnchanged_NoBak()
    {
        const string v7 = """
        { "schemaVersion": 8, "connection": { "type": "Serial", "serial": { "comPort": "COM7" } },
          "sliders": [],
          "buttons": [ { "index": 0, "actions": { "mode-2": { "singlePress": { "action": "GoToMode", "modeId": "default" } } } } ],
          "modes": [ { "id": "default", "name": "Default" }, { "id": "mode-2", "name": "Calls" } ] }
        """;
        var c = Load(v7).Config;

        Assert.Equal(AppConfig.CurrentSchemaVersion, c.SchemaVersion);
        Assert.Equal(new[] { "default", "mode-2" }, c.Modes.Select(m => m.Id));
        var action = c.Buttons[0].BindingFor("mode-2").SinglePress;
        Assert.Equal(ButtonActionType.GoToMode, action.Action);
        Assert.Equal("default", action.ModeId);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.bak"));
        Assert.Equal(v7, File.ReadAllText(_configPath));   // not rewritten
    }

    [Fact]
    public void Rename_IsIdempotent()
    {
        var root = (JsonObject)JsonNode.Parse(V6Json())!;
        ConfigMigrator.RenamePresetsToModes(root);
        var once = root.ToJsonString();
        ConfigMigrator.RenamePresetsToModes(root);

        Assert.Equal(once, root.ToJsonString());
        Assert.False(root.ContainsKey("presets"));
        Assert.Equal(2, root["modes"]!.AsArray().Count);
        var slots = root["buttons"]![0]!["actions"]!["default"]!;
        Assert.Equal("GoToMode", slots["singlePress"]!["action"]!.GetValue<string>());
        Assert.Equal("preset-2", slots["singlePress"]!["modeId"]!.GetValue<string>());
        Assert.False(slots["singlePress"]!.AsObject().ContainsKey("presetId"));
        Assert.Equal("NextMode", slots["longPress"]!["action"]!.GetValue<string>());
        Assert.Equal("None", slots["doublePress"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void ScriptEnvironment_HasVolcoMode_NotVolcoPreset()
    {
        var env = new ActionOrigin(2, PressType.Long, "Gaming").Environment();

        Assert.Equal("Gaming", env["VOLCO_MODE"]);
        Assert.False(env.ContainsKey("VOLCO_PRESET"));
        Assert.Equal(new[] { "VOLCO_BUTTON", "VOLCO_GESTURE", "VOLCO_MODE" }, env.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ChooseMode_Text()
    {
        Assert.Equal("Choose a mode", ControllerDisplay.ChooseMode);
    }
}
