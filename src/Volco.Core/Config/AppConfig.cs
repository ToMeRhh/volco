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
    NextMode,
    GoToMode,
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

    /// <summary>Where each of <see cref="Processes"/>' exes is (lower-case exe name → full path), only
    /// for its icon while it is not running: learned from its audio session (the latest path seen
    /// wins, so an app that moved follows) or picked with "Other app…". Sessions are still matched
    /// by exe name alone.</summary>
    public Dictionary<string, string> AppPaths { get; set; } = new();

    /// <summary>The saved exe path of <paramref name="app"/>, or null.</summary>
    public string? AppPathFor(string app)
        => AppPaths.TryGetValue(app.ToLowerInvariant(), out var path) && path.Length > 0 ? path : null;

    /// <summary>Saves where <paramref name="app"/>'s exe is; returns whether anything changed.</summary>
    public bool RememberAppPath(string app, string path)
    {
        if (app.Length == 0 || path.Length == 0 || AppPathFor(app) == path) return false;
        AppPaths[app.ToLowerInvariant()] = path;
        return true;
    }

    public bool Invert { get; set; }
    public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
    public double DeadZonePercent { get; set; } = 0;   // 0..0.2

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

    /// <summary>The target <see cref="ModeConfig.Id"/> for GoToMode.</summary>
    public string? ModeId { get; set; }

    /// <summary>The app a media action controls; null = whatever Windows considers current.</summary>
    public MediaAppConfig? MediaApp { get; set; }

    /// <summary>The program for OpenProgram, the script for RunScript.</summary>
    public CommandConfig? Command { get; set; }

    /// <summary>RunScript: what a press does while a run of this action is still going.</summary>
    public WhileRunningPolicy WhileRunning { get; set; } = WhileRunningPolicy.Ignore;
}

/// <summary>What one button does in one mode.</summary>
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

public class ModeConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ButtonConfig
{
    /// <summary>Always equal to the button's position in <see cref="AppConfig.Buttons"/>, which is
    /// its hardware input number. Rewritten on save; never read for routing.</summary>
    public int Index { get; set; }

    /// <summary>Per-mode actions, keyed by <see cref="ModeConfig.Id"/>. A missing key means the
    /// button does nothing in that mode. Timing below is shared by every mode.</summary>
    public Dictionary<string, ButtonBindingConfig> Actions { get; set; } = new();

    public int LongPressMs { get; set; } = 500;
    public int DoublePressWindowMs { get; set; } = 300;

    /// <summary>The binding for <paramref name="modeId"/>, or an empty one (not stored) when the
    /// mode has none.</summary>
    public ButtonBindingConfig BindingFor(string modeId)
        => Actions.TryGetValue(modeId, out var b) && b != null ? b : new ButtonBindingConfig();

    /// <summary>The stored binding for <paramref name="modeId"/>, created if missing.</summary>
    public ButtonBindingConfig GetOrAddBinding(string modeId)
    {
        if (!Actions.TryGetValue(modeId, out var b) || b == null)
            Actions[modeId] = b = new ButtonBindingConfig();
        return b;
    }
}

public class AppConfig
{
    public const int CurrentSchemaVersion = 7;

    /// <summary>Id of the mode every migrated or new config starts with.</summary>
    public const string DefaultModeId = "default";

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

    /// <summary>Button modes, in tab order. The first one is live when the app starts.</summary>
    public List<ModeConfig> Modes { get; set; } = new();

    /// <summary>Device nicknames and icons (the device dialog). Only customized devices.</summary>
    public List<DeviceSettingsConfig> Devices { get; set; } = new();

    /// <summary>The last Hello the controller sent; null until the first one.</summary>
    public ControllerInfoConfig? Controller { get; set; }

    /// <summary>The mirror (spec §8.2): base64 of the controller's config bytes exactly as stored.
    /// Written only by <c>ControllerConfigService</c>, on a confirmed save or an adopted read.</summary>
    public string? ControllerConfigBlob { get; set; }

    /// <summary>The saved exe path of <paramref name="app"/> (an exe name) on the apps slider that
    /// lists it (<see cref="SliderConfig.AppPaths"/>), or null. An app is on one slider at most.</summary>
    public string? AppPathFor(string app)
        => Sliders.Where(s => s.Type == SliderMappingType.Processes).Select(s => s.AppPathFor(app)).FirstOrDefault(p => p is not null);

    /// <summary>Saves where <paramref name="app"/>'s exe is on the apps slider that lists it (its
    /// session was seen there); returns whether anything changed.</summary>
    public bool RememberAppPath(string app, string path)
    {
        var changed = false;
        foreach (var slider in Sliders)
            if (slider.Type == SliderMappingType.Processes && slider.Processes.Contains(app, StringComparer.OrdinalIgnoreCase))
                changed |= slider.RememberAppPath(app, path);
        return changed;
    }

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
        config.Modes.Add(new ModeConfig { Id = DefaultModeId, Name = "Default" });
        return config;
    }

    /// <summary>Repairs invariants a hand-edited or older file may break: at least one mode,
    /// no null action maps, and Index equal to list position.</summary>
    public void Normalize()
    {
        // A hand-edited "sliders": null (etc.) deserialises to null despite the initialisers.
        Sliders ??= new();
        Buttons ??= new();
        Modes ??= new();
        Devices ??= new();
        Devices.RemoveAll(d => d is null || string.IsNullOrEmpty(d.Id));
        if (Modes.Count == 0)
            Modes.Add(new ModeConfig { Id = DefaultModeId, Name = "Default" });
        for (int i = 0; i < Sliders.Count; i++)
            Sliders[i].Index = i;
        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Index = i;
            Buttons[i].Actions ??= new();
        }
    }
}
