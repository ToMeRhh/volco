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

    // ── Fix A: doublePressWindowMs <= 0 fires Single synchronously, and per-call timings ──────

    [Fact]
    public void Window0_SinglePress_FiresSynchronouslyBeforeHandleButtonEventReturns()
    {
        _detector.HandleButtonEvent(5, ButtonState.Pressed, longPressMs: 500, doublePressWindowMs: 0);
        _detector.HandleButtonEvent(5, ButtonState.Released, longPressMs: 500, doublePressWindowMs: 0);

        // No WaitForEventsAsync at all: with no double-press window there is nothing to wait for,
        // so the Single must already have been raised by the time Release's HandleButtonEvent
        // call returns to us, on this thread.
        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Single, _events[0].PressType);
            Assert.Equal(5, _events[0].ButtonIndex);
        }
    }

    [Fact]
    public void Window0_TwoQuickPressReleasePairs_RaiseTwoSinglesAndNoDouble()
    {
        _detector.HandleButtonEvent(6, ButtonState.Pressed, 500, 0);
        _detector.HandleButtonEvent(6, ButtonState.Released, 500, 0);
        _detector.HandleButtonEvent(6, ButtonState.Pressed, 500, 0);
        _detector.HandleButtonEvent(6, ButtonState.Released, 500, 0);

        lock (_eventsLock)
        {
            Assert.Equal(2, _events.Count);
            Assert.All(_events, e => Assert.Equal(PressType.Single, e.PressType));
        }
    }

    [Fact]
    public async Task TrackerTimingIsNotFrozenAtFirstUse_SwitchingToWindow0TakesEffectImmediately()
    {
        // First cycle for this button uses a real double-press window (300ms) - before the fix,
        // _trackers.GetOrAdd froze this tracker's timings at 300ms for its whole lifetime.
        _detector.HandleButtonEvent(7, ButtonState.Pressed, 500, 300);
        _detector.HandleButtonEvent(7, ButtonState.Released, 500, 300);
        await WaitForEventsAsync(1);

        lock (_eventsLock)
        {
            Assert.Single(_events);
            Assert.Equal(PressType.Single, _events[0].PressType);
        }

        // Second cycle, same button, now with window 0: if the timing were frozen at 300ms this
        // would still be waiting and the synchronous assert below would see only 1 event.
        _detector.HandleButtonEvent(7, ButtonState.Pressed, 500, 0);
        _detector.HandleButtonEvent(7, ButtonState.Released, 500, 0);

        lock (_eventsLock)
        {
            Assert.Equal(2, _events.Count);
            Assert.Equal(PressType.Single, _events[1].PressType);
        }
    }
}
