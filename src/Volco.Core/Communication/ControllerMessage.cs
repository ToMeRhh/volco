namespace Volco.Core.Communication;

/// <summary>
/// Wire message types (spec §3.2). The high bit is the direction: 0x0_ comes from the controller,
/// 0x8_ from the PC. Paired messages share their low bits (Hello/HelloRequest, Alive/Heartbeat).
/// </summary>
public enum MessageType : byte
{
    Hello = 0x01,
    Alive = 0x02,
    Slider = 0x03,
    Button = 0x04,
    HelloRequest = 0x81,
    Heartbeat = 0x82,
    Led = 0x83,
    Screen = 0x84,
    Begin = 0x85,
    Commit = 0x86,
    ConfigData = 0x05,
    ConfigResult = 0x06,
    ConfigRead = 0x87,
    ConfigWriteBegin = 0x88,
    ConfigWriteChunk = 0x89,
    ConfigWriteEnd = 0x8A,
}

/// <summary>The protocol version this app speaks; Hello carries the firmware's.</summary>
public static class ProtocolVersion
{
    public const byte Current = 1;
}

/// <summary>The controller's chip family, from Hello (spec §5.2).</summary>
public enum ChipFamily : byte
{
    Unknown = 0,
    Esp32 = 1,
}

/// <summary>What the controller runs, from Hello (spec §5.2): its stored config, or the built-in
/// defaults because none is stored or the stored one couldn't be used.</summary>
public enum ControllerConfigState : byte
{
    None = 0,
    Stored = 1,
    Damaged = 2,
}

public enum ButtonState : byte
{
    Released = 0x00,
    Pressed = 0x01
}

public abstract class ControllerMessage
{
    public MessageType Type { get; }
    protected ControllerMessage(MessageType type) => Type = type;
}

public class SliderMessage : ControllerMessage
{
    public int SliderIndex { get; }
    public ushort Value { get; }

    public SliderMessage(int sliderIndex, ushort value) : base(MessageType.Slider)
    {
        SliderIndex = sliderIndex;
        Value = value;
    }
}

public class ButtonMessage : ControllerMessage
{
    public int ButtonIndex { get; }
    public ButtonState State { get; }

    public ButtonMessage(int buttonIndex, ButtonState state) : base(MessageType.Button)
    {
        ButtonIndex = buttonIndex;
        State = state;
    }
}

/// <summary>The controller describing its hardware (spec §3.2): sent at boot, in reply to every
/// HelloRequest, and on the first frame after a host timeout.</summary>
public sealed class HelloMessage : ControllerMessage
{
    public byte Version { get; }
    public ChipFamily Chip { get; }

    /// <summary>The firmware build id (spec §9.2), e.g. a 12-hex-digit source hash, or "dev".</summary>
    public string FirmwareVersion { get; }
    public ControllerConfigState ConfigState { get; }

    /// <summary>CRC-32 of the controller's stored config bytes (spec §6.5); 0 when none.</summary>
    public uint ConfigCrc { get; }
    public int SliderCount { get; }

    /// <summary>One indicator kind per button, by hardware input number.</summary>
    public IReadOnlyList<IndicatorKind> Buttons { get; }

    public HelloMessage(byte version, ChipFamily chip, string firmwareVersion, ControllerConfigState configState,
                        uint configCrc, int sliderCount, IReadOnlyList<IndicatorKind> buttons) : base(MessageType.Hello)
    {
        Version = version;
        Chip = chip;
        FirmwareVersion = firmwareVersion;
        ConfigState = configState;
        ConfigCrc = configCrc;
        SliderCount = sliderCount;
        Buttons = buttons;
    }

    /// <summary>A Hello of a version this app doesn't speak (spec §5.2): only the version is known.</summary>
    public static HelloMessage VersionOnly(byte version)
        => new(version, ChipFamily.Unknown, "", ControllerConfigState.None, 0, 0, Array.Empty<IndicatorKind>());

    /// <summary>Tests: a version-1 ESP32 with a stored config.</summary>
    public HelloMessage(byte version, int sliderCount, IReadOnlyList<IndicatorKind> buttons)
        : this(version, ChipFamily.Esp32, "", ControllerConfigState.Stored, 0, sliderCount, buttons) { }
}

/// <summary>One chunk of the controller's reply to a ConfigRead (spec §5.3): the stored bytes from
/// <see cref="Offset"/>, out of <see cref="Total"/>; <see cref="Crc32"/> is the whole blob's.</summary>
public sealed class ConfigDataMessage : ControllerMessage
{
    public byte Seq { get; }
    public ushort Total { get; }
    public ushort Offset { get; }
    public uint Crc32 { get; }
    public byte[] Data { get; }

    public ConfigDataMessage(byte seq, ushort total, ushort offset, uint crc32, byte[] data) : base(MessageType.ConfigData)
        => (Seq, Total, Offset, Crc32, Data) = (seq, total, offset, crc32, data);
}

/// <summary>The controller's answer to a config write (spec §5.4).</summary>
public sealed class ConfigResultMessage : ControllerMessage
{
    public byte Seq { get; }
    public Volco.Core.Controller.ConfigResultCode Code { get; }
    public byte Item { get; }
    public byte Field { get; }

    public ConfigResultMessage(byte seq, Volco.Core.Controller.ConfigResultCode code, byte item, byte field)
        : base(MessageType.ConfigResult) => (Seq, Code, Item, Field) = (seq, code, item, field);
}

/// <summary>The controller's keepalive: sent when it has sent nothing else for a second.</summary>
public sealed class AliveMessage : ControllerMessage
{
    public AliveMessage() : base(MessageType.Alive) { }
}
