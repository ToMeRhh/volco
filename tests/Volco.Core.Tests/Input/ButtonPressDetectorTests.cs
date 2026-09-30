using Volco.Core.Communication;
using Volco.Core.Input;
using Xunit;
using System.Diagnostics;

namespace Volco.Core.Tests.Input;

public class ButtonPressDetectorTests
{
    private readonly ButtonPressDetector _detector;
    private readonly List<(int ButtonIndex, PressType PressType)> _events = new();
    private readonly object _eventsLock = new();

    public ButtonPressDetectorTests()
    {
        _detector = new ButtonPressDetector(
            longPressMs: 500,
            doublePressWindowMs: 300
        );
        _detector.PressDetected += (_, e) =>
        {
            lock (_eventsLock)
            {
                _events.Add((e.ButtonIndex, e.PressType));
            }
        };
    }

    private async Task WaitForEventsAsync(int expectedCount, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_eventsLock)
            {
                if (_events.Count >= expectedCount)
                    return;
            }
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task SinglePress_DetectedAfterTimeout()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);

        await WaitForEventsAsync(1);

        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Single, _events[0].PressType);
            Assert.Equal(0, _events[0].ButtonIndex);
        }
    }

    [Fact]
    public async Task DoublePress_DetectedOnSecondRelease()
    {
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);
        await Task.Delay(100);
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);

        await WaitForEventsAsync(1);

        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Double, _events[0].PressType);
            Assert.Equal(1, _events[0].ButtonIndex);
        }
    }

    [Fact]
    public async Task LongPress_DetectedWhileHeld()
    {
        _detector.HandleButtonEvent(2, ButtonState.Pressed);

        await WaitForEventsAsync(1);

        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Long, _events[0].PressType);
            Assert.Equal(2, _events[0].ButtonIndex);
        }

        _detector.HandleButtonEvent(2, ButtonState.Released);
        await Task.Delay(400); // Wait to see if any extra (incorrect) events fire

        lock (_eventsLock)
        {
            Assert.Single(_events);
        }
    }

    [Fact]
    public async Task IndependentButtons_DoNotInterfere()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);

        await WaitForEventsAsync(2);

        lock (_eventsLock)
        {
            Assert.Equal(2, _events.Count);
            var orderedEvents = _events.OrderBy(e => e.ButtonIndex).ToList();
            Assert.Equal(0, orderedEvents[0].ButtonIndex);
            Assert.Equal(1, orderedEvents[1].ButtonIndex);
            Assert.All(_events, e => Assert.Equal(PressType.Single, e.PressType));
        }
    }

    [Fact]
    public async Task PerButtonThresholds_ShortLongPress_FiresSooner()
    {
        _detector.HandleButtonEvent(3, ButtonState.Pressed, longPressMs: 120, doublePressWindowMs: 300);
        await WaitForEventsAsync(1, timeoutMs: 400);
        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Long, _events[0].PressType);
        }
        _detector.HandleButtonEvent(3, ButtonState.Released, 120, 300);
    }
}
