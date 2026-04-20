using System.Text.Json.Serialization;

namespace Volco.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionType { Serial, Udp, WebSocket }

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
    public int BaudRate { get; set; } = 9600;
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

public class SliderConfig
{
    public int Index { get; set; }
    public SliderMappingType Type { get; set; } = SliderMappingType.Device;
    public List<string> Processes { get; set; } = new();
    public string DeviceName { get; set; } = "";
    public bool Invert { get; set; }
}

public class ButtonActionConfig
{
    public ButtonActionType Action { get; set; } = ButtonActionType.None;
    public string? DeviceId { get; set; }
}

public class ButtonConfig
{
    public int Index { get; set; }
    public ButtonActionConfig SinglePress { get; set; } = new();
    public ButtonActionConfig DoublePress { get; set; } = new();
    public ButtonActionConfig LongPress { get; set; } = new();
}

public class OutputDevicesConfig
{
    public string Device1 { get; set; } = "";
    public string Device2 { get; set; } = "";
}

public class AppConfig
{
    public ConnectionConfig Connection { get; set; } = new();
    public List<SliderConfig> Sliders { get; set; } = new();
    public List<ButtonConfig> Buttons { get; set; } = new();
    public OutputDevicesConfig OutputDevices { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        for (int i = 0; i < 5; i++)
            config.Sliders.Add(new SliderConfig { Index = i });
        for (int i = 0; i < 3; i++)
            config.Buttons.Add(new ButtonConfig { Index = i });
        return config;
    }
}
