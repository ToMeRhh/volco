using System.Text.Json.Serialization;

namespace Volco.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionType { Serial, Udp, WebSocket }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResponseCurve { Linear, Logarithmic, Exponential, SCurve }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SliderMappingType { Processes, Device, Unmapped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ButtonActionType
{
    None,
    MuteSpecificDevice,
    MuteCurrentOutput,
    MuteCurrentInput,
    ToggleOutputDevice,
    SetOutputDevice
}

public class SerialConfig
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
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
    public DeviceRefConfig? Device { get; set; }
}

public class ButtonConfig
{
    public int Index { get; set; }
    public ButtonActionConfig SinglePress { get; set; } = new();
    public ButtonActionConfig DoublePress { get; set; } = new();
    public ButtonActionConfig LongPress { get; set; } = new();
    public int LongPressMs { get; set; } = 500;
    public int DoublePressWindowMs { get; set; } = 300;
}

public class OutputDevicesConfig
{
    public DeviceRefConfig Device1 { get; set; } = new();
    public DeviceRefConfig Device2 { get; set; } = new();
}

public class UpdateConfig
{
    /// <summary>
    /// Optional GitHub Personal Access Token (needs <c>actions:read</c>) used to download CI
    /// build artifacts. Not required for updating from published releases.
    /// </summary>
    public string? GitHubToken { get; set; }

    /// <summary>Remembers whether the user prefers updating from the latest CI artifact.</summary>
    public bool UseCiArtifact { get; set; }
}

public class AppConfig
{
    public const int CurrentSchemaVersion = 2;

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
    public OutputDevicesConfig OutputDevices { get; set; } = new();
    public UpdateConfig Updates { get; set; } = new();
    public bool DebugLogging { get; set; } = false;

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig { SchemaVersion = CurrentSchemaVersion };
        for (int i = 0; i < 5; i++)
            config.Sliders.Add(new SliderConfig { Index = i });
        for (int i = 0; i < 3; i++)
            config.Buttons.Add(new ButtonConfig { Index = i });
        return config;
    }
}
