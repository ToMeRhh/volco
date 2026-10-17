using System.Text.Json.Serialization;

namespace Volco.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionType>))]
public enum ConnectionType { Serial, Udp, WebSocket }

[JsonConverter(typeof(JsonStringEnumConverter<ResponseCurve>))]
public enum ResponseCurve { Linear, Logarithmic, Exponential, SCurve }

[JsonConverter(typeof(JsonStringEnumConverter<SliderMappingType>))]
public enum SliderMappingType
{
    Processes,
    Device,
    /// <summary>Every app that no Processes slider lists ("All other apps" in the UI).</summary>
    Unmapped,
    /// <summary>"Not set up": the slider changes nothing.</summary>
    None
}

[JsonConverter(typeof(JsonStringEnumConverter<ButtonActionType>))]
public enum ButtonActionType
{
    None,
    MuteSpecificDevice,
    MuteCurrentOutput,
    MuteCurrentInput,
    ToggleOutputDevice,
    SetOutputDevice,
    NextPreset,
    GoToPreset,
    MediaPlayPause,
    MediaNext,
    MediaPrevious,
    MediaStop,
    OpenProgram,
    RunScript
}

/// <summary>Groups of <see cref="ButtonActionType"/> that share settings or rules.</summary>
public static class ButtonActionTypes
{
    /// <summary>Play/Pause, Next, Previous, Stop: they take a <see cref="ButtonActionConfig.MediaApp"/>.</summary>
    public static bool IsMedia(ButtonActionType t) => t is ButtonActionType.MediaPlayPause or ButtonActionType.MediaNext
        or ButtonActionType.MediaPrevious or ButtonActionType.MediaStop;

    /// <summary>Open a program, Run a script: they run code on this PC, so a press runs them only
    /// when it came over USB serial (custom-actions spec §7). They take a <see cref="ButtonActionConfig.Command"/>.</summary>
    public static bool RunsCode(ButtonActionType t) => t is ButtonActionType.OpenProgram or ButtonActionType.RunScript;
}

/// <summary>What a script button does when pressed while its script still runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WhileRunningPolicy>))]
public enum WhileRunningPolicy
{
    Ignore,
    StartAnother
}

/// <summary>The app a media action controls: its Windows app id (AppUserModelId) and display name.</summary>
public class MediaAppConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A program or script to run: a file path or URL, its arguments and working folder,
/// and an optional name for the screen.</summary>
public class CommandConfig
{
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>Empty: the file's own folder.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Empty: the file name without its extension.</summary>
    public string Name { get; set; } = "";
}

public class SerialConfig
{
    public string ComPort { get; set; } = "COM3";
}

public class UdpConfig
{
    public int ListenPort { get; set; } = 12345;
}

public class WebSocketConfig
{
    public int Port { get; set; } = 8765;
}

public class ConnectionConfig
{
    public ConnectionType Type { get; set; } = ConnectionType.Serial;
    public SerialConfig Serial { get; set; } = new();
    public UdpConfig Udp { get; set; } = new();
    public WebSocketConfig WebSocket { get; set; } = new();
}

public partial class SliderConfig
{
    public int Index { get; set; }

    /// <summary>The user's name for this slider; "" means unnamed.</summary>
    public string Name { get; set; } = "";

    public SliderMappingType Type { get; set; } = SliderMappingType.Device;
    public List<string> Processes { get; set; } = new();

    /// <summary>null means "the current default output device".</summary>
    public DeviceRefConfig? Device { get; set; }

    public bool Invert { get; set; }
    public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
    public double DeadZonePercent { get; set; } = 0;   // 0..0.2
    public double SmoothingFactor { get; set; } = 0;    // 0..1 (0 = off)

    /// <summary>Display name for the configured device; "" means the current default output.</summary>
    [JsonIgnore]
    public string DeviceName => Device?.FriendlyName ?? "";
}

public class ButtonActionConfig
{
    public ButtonActionType Action { get; set; } = ButtonActionType.None;

    /// <summary>The device for MuteSpecificDevice/SetOutputDevice; output A for ToggleOutputDevice.</summary>
    public DeviceRefConfig? Device { get; set; }

    /// <summary>Output B for ToggleOutputDevice.</summary>
    public DeviceRefConfig? DeviceB { get; set; }

    /// <summary>The target <see cref="PresetConfig.Id"/> for GoToPreset.</summary>
    public string? PresetId { get; set; }

    /// <summary>The app a media action controls; null = whatever Windows considers current.</summary>
    public MediaAppConfig? MediaApp { get; set; }

    /// <summary>The program for OpenProgram, the script for RunScript.</summary>
    public CommandConfig? Command { get; set; }

    /// <summary>RunScript: what a press does while a run of this action is still going.</summary>
    public WhileRunningPolicy WhileRunning { get; set; } = WhileRunningPolicy.Ignore;
}

/// <summary>What one button does in one preset.</summary>
public class ButtonBindingConfig
{
    public ButtonActionConfig SinglePress { get; set; } = new();
    public ButtonActionConfig DoublePress { get; set; } = new();
    public ButtonActionConfig LongPress { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => SinglePress.Action == ButtonActionType.None
                           && DoublePress.Action == ButtonActionType.None
                           && LongPress.Action == ButtonActionType.None;

    /// <summary>Single, double, long: the order the controller-feedback rule walks them in.</summary>
    public IEnumerable<ButtonActionConfig> All()
    {
        yield return SinglePress;
        yield return DoublePress;
        yield return LongPress;
    }
}

public class PresetConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ButtonConfig
{
    /// <summary>Always equal to the button's position in <see cref="AppConfig.Buttons"/>, which is
    /// its hardware input number. Rewritten on save; never read for routing.</summary>
    public int Index { get; set; }

    /// <summary>Per-preset actions, keyed by <see cref="PresetConfig.Id"/>. A missing key means the
    /// button does nothing in that preset. Timing below is shared by every preset.</summary>
    public Dictionary<string, ButtonBindingConfig> Actions { get; set; } = new();

    public int LongPressMs { get; set; } = 500;
    public int DoublePressWindowMs { get; set; } = 300;

    /// <summary>The binding for <paramref name="presetId"/>, or an empty one (not stored) when the
    /// preset has none.</summary>
    public ButtonBindingConfig BindingFor(string presetId)
        => Actions.TryGetValue(presetId, out var b) && b != null ? b : new ButtonBindingConfig();

    /// <summary>The stored binding for <paramref name="presetId"/>, created if missing.</summary>
    public ButtonBindingConfig GetOrAddBinding(string presetId)
    {
        if (!Actions.TryGetValue(presetId, out var b) || b == null)
            Actions[presetId] = b = new ButtonBindingConfig();
        return b;
    }
}

public class AppConfig
{
    public const int CurrentSchemaVersion = 6;

    /// <summary>Id of the preset every migrated or new config starts with.</summary>
    public const string DefaultPresetId = "default";

    /// <summary>
    /// Absent or 0 means schema v1 (device references stored as bare names). Defaults to 0
    /// rather than <see cref="CurrentSchemaVersion"/> so that deserializing a real v1
    /// config.json (which has no "schemaVersion" key) is distinguishable from an
    /// already-migrated one. <see cref="CreateDefault"/> stamps the current version explicitly
    /// for brand-new configs.
    /// </summary>
    public int SchemaVersion { get; set; } = 0;

    public ConnectionConfig Connection { get; set; } = new();
    public List<SliderConfig> Sliders { get; set; } = new();
    public List<ButtonConfig> Buttons { get; set; } = new();

    /// <summary>Button presets, in tab order. The first one is live when the app starts.</summary>
    public List<PresetConfig> Presets { get; set; } = new();

    /// <summary>Device nicknames and icons (Devices page). Only customized devices.</summary>
    public List<DeviceSettingsConfig> Devices { get; set; } = new();

    /// <summary>The last Hello the controller sent; null until the first one.</summary>
    public ControllerInfoConfig? Controller { get; set; }

    /// <summary>The mirror (spec §8.2): base64 of the controller's config bytes exactly as stored.
    /// Written only by <c>ControllerConfigService</c>, on a confirmed save or an adopted read.</summary>
    public string? ControllerConfigBlob { get; set; }

    /// <summary>The settings entry for an endpoint id, or null.</summary>
    public DeviceSettingsConfig? FindDevice(string? id)
        => string.IsNullOrEmpty(id) ? null
           : Devices.Find(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    public bool DebugLogging { get; set; } = false;

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig { SchemaVersion = CurrentSchemaVersion };
        for (int i = 0; i < 5; i++)
            config.Sliders.Add(new SliderConfig { Index = i, Type = SliderMappingType.None });
        for (int i = 0; i < 3; i++)
            config.Buttons.Add(new ButtonConfig { Index = i });
        config.Presets.Add(new PresetConfig { Id = DefaultPresetId, Name = "Default" });
        return config;
    }

    /// <summary>Repairs invariants a hand-edited or older file may break: at least one preset,
    /// no null action maps, and Index equal to list position.</summary>
    public void Normalize()
    {
        // A hand-edited "sliders": null (etc.) deserialises to null despite the initialisers.
        Sliders ??= new();
        Buttons ??= new();
        Presets ??= new();
        Devices ??= new();
        Devices.RemoveAll(d => d is null || string.IsNullOrEmpty(d.Id));
        if (Presets.Count == 0)
            Presets.Add(new PresetConfig { Id = DefaultPresetId, Name = "Default" });
        for (int i = 0; i < Sliders.Count; i++)
            Sliders[i].Index = i;
        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Index = i;
            Buttons[i].Actions ??= new();
        }
    }
}
