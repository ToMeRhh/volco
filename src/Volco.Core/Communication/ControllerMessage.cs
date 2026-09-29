namespace Volco.Core.Communication;

public enum MessageType : byte
{
    Slider = 0x01,
    Button = 0x02,
    MuteState = 0x03,
    ActiveDevice = 0x04
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

public class MuteStateMessage : ControllerMessage
{
    public int ButtonIndex { get; }
    public bool IsMuted { get; }

    public MuteStateMessage(int buttonIndex, bool isMuted) : base(MessageType.MuteState)
    {
        ButtonIndex = buttonIndex;
        IsMuted = isMuted;
    }
}

public class ActiveDeviceMessage : ControllerMessage
{
    public int DeviceIndex { get; }

    public ActiveDeviceMessage(int deviceIndex) : base(MessageType.ActiveDevice)
    {
        DeviceIndex = deviceIndex;
    }
}
