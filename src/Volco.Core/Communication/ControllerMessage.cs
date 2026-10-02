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
}

/// <summary>The protocol version this app speaks; Hello carries the firmware's.</summary>
public static class ProtocolVersion
{
    public const byte Current = 1;
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
    public int SliderCount { get; }

    /// <summary>One indicator kind per button, by hardware input number.</summary>
    public IReadOnlyList<IndicatorKind> Buttons { get; }

    public HelloMessage(byte version, int sliderCount, IReadOnlyList<IndicatorKind> buttons) : base(MessageType.Hello)
    {
        Version = version;
        SliderCount = sliderCount;
        Buttons = buttons;
    }
}

/// <summary>The controller's keepalive: sent when it has sent nothing else for a second.</summary>
public sealed class AliveMessage : ControllerMessage
{
    public AliveMessage() : base(MessageType.Alive) { }
}
