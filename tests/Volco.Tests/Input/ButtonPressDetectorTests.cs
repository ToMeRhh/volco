using Volco.Communication;
using Volco.Input;
using Xunit;

namespace Volco.Tests.Input;

public class ButtonPressDetectorTests
{
    private readonly ButtonPressDetector _detector;
    private readonly List<(int ButtonIndex, PressType PressType)> _events = new();

    public ButtonPressDetectorTests()
    {
        _detector = new ButtonPressDetector(
            longPressMs: 500,
            doublePressWindowMs: 300
        );
        _detector.PressDetected += (_, e) => _events.Add((e.ButtonIndex, e.PressType));
    }

    [Fact]
    public async Task SinglePress_DetectedAfterTimeout()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);
        await Task.Delay(400);

        Assert.Single(_events);
        Assert.Equal(PressType.Single, _events[0].PressType);
        Assert.Equal(0, _events[0].ButtonIndex);
    }

    [Fact]
    public async Task DoublePress_DetectedOnSecondRelease()
    {
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);
        await Task.Delay(100);
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);
        await Task.Delay(50);

        Assert.Single(_events);
        Assert.Equal(PressType.Double, _events[0].PressType);
        Assert.Equal(1, _events[0].ButtonIndex);
    }

    [Fact]
    public async Task LongPress_DetectedWhileHeld()
    {
        _detector.HandleButtonEvent(2, ButtonState.Pressed);
        await Task.Delay(600);

        Assert.Single(_events);
        Assert.Equal(PressType.Long, _events[0].PressType);
        Assert.Equal(2, _events[0].ButtonIndex);

        _detector.HandleButtonEvent(2, ButtonState.Released);
        await Task.Delay(400);

        Assert.Single(_events);
    }

    [Fact]
    public async Task IndependentButtons_DoNotInterfere()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);
        await Task.Delay(600);

        Assert.Equal(2, _events.Count);
        Assert.Equal(0, _events[0].ButtonIndex);
        Assert.Equal(1, _events[1].ButtonIndex);
        Assert.All(_events, e => Assert.Equal(PressType.Single, e.PressType));
    }
}
