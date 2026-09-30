using System.Collections.Concurrent;
using Volco.Core.Communication;

namespace Volco.Core.Input;

public enum PressType { Single, Double, Long }

public class ButtonPressEventArgs : EventArgs
{
    public int ButtonIndex { get; }
    public PressType PressType { get; }

    public ButtonPressEventArgs(int buttonIndex, PressType pressType)
    {
        ButtonIndex = buttonIndex;
        PressType = pressType;
    }
}

public class ButtonPressDetector
{
    private readonly int _longPressMs;
    private readonly int _doublePressWindowMs;
    private readonly ConcurrentDictionary<int, ButtonStateTracker> _trackers = new();

    public event EventHandler<ButtonPressEventArgs>? PressDetected;

    public ButtonPressDetector(int longPressMs = 500, int doublePressWindowMs = 300)
    {
        _longPressMs = longPressMs;
        _doublePressWindowMs = doublePressWindowMs;
    }

    public void HandleButtonEvent(int buttonIndex, ButtonState state)
        => HandleButtonEvent(buttonIndex, state, _longPressMs, _doublePressWindowMs);

    public void HandleButtonEvent(int buttonIndex, ButtonState state, int longPressMs, int doublePressWindowMs)
    {
        // Fix A (secondary bug): the tracker is created once per button and lives for the
        // process lifetime, but its timings must NOT be frozen at that first creation - a
        // settings change to LongPressMs/DoublePressWindowMs must take effect on the very next
        // press. GetOrAdd only decides identity; HandleState below takes the timings from THIS
        // call, every call.
        var tracker = _trackers.GetOrAdd(buttonIndex, idx => new ButtonStateTracker(idx, OnPressDetected));
        tracker.HandleState(state, longPressMs, doublePressWindowMs);
    }

    private void OnPressDetected(int buttonIndex, PressType pressType)
    {
        PressDetected?.Invoke(this, new ButtonPressEventArgs(buttonIndex, pressType));
    }

    private class ButtonStateTracker
    {
        private readonly int _buttonIndex;
        private readonly Action<int, PressType> _callback;
        private readonly object _lock = new();
        private CancellationTokenSource? _longPressCts;
        private CancellationTokenSource? _singlePressCts;
        private bool _longPressFired;
        private bool _waitingForDoublePress;

        // Fix A: set on every HandleState call (under _lock), not just once at construction -
        // see the comment on HandleButtonEvent above.
        private int _longPressMs;
        private int _doublePressWindowMs;

        public ButtonStateTracker(int buttonIndex, Action<int, PressType> callback)
        {
            _buttonIndex = buttonIndex;
            _callback = callback;
        }

        public void HandleState(ButtonState state, int longPressMs, int doublePressWindowMs)
        {
            lock (_lock)
            {
                _longPressMs = longPressMs;
                _doublePressWindowMs = doublePressWindowMs;

                if (state == ButtonState.Pressed)
                    OnPressed();
                else
                    OnReleased();
            }
        }

        private void OnPressed()
        {
            _longPressFired = false;

            _longPressCts?.Cancel();
            _longPressCts?.Dispose();

            _singlePressCts?.Cancel();
            _singlePressCts?.Dispose();
            _singlePressCts = null;

            _longPressCts = new CancellationTokenSource();
            _ = LongPressTimerAsync(_longPressCts.Token, _longPressMs);
        }

        private void OnReleased()
        {
            _longPressCts?.Cancel();
            _longPressCts?.Dispose();
            _longPressCts = null;

            if (_longPressFired)
            {
                _longPressFired = false;
                return;
            }

            if (_waitingForDoublePress)
            {
                _singlePressCts?.Cancel();
                _singlePressCts?.Dispose();
                _singlePressCts = null;
                _waitingForDoublePress = false;
                _callback(_buttonIndex, PressType.Double);
                return;
            }

            // Fix A (primary bug): no double-press action is configured for this button
            // (doublePressWindowMs <= 0, set by VolcoRuntime.OnMessageReceived), so there is
            // nothing to wait for - fire Single synchronously, right here, with no timer and no
            // latency. _waitingForDoublePress is deliberately left false: a second press arriving
            // a moment later must classify on its own terms (as another immediate Single), not
            // pair up with this one as a Double that was never configured.
            if (_doublePressWindowMs <= 0)
            {
                _callback(_buttonIndex, PressType.Single);
                return;
            }

            _waitingForDoublePress = true;
            _singlePressCts?.Cancel();
            _singlePressCts?.Dispose();
            _singlePressCts = new CancellationTokenSource();
            _ = SinglePressTimerAsync(_singlePressCts.Token, _doublePressWindowMs);
        }

        private async Task LongPressTimerAsync(CancellationToken ct, int longPressMs)
        {
            try
            {
                await Task.Delay(longPressMs, ct);
                lock (_lock)
                {
                    if (ct.IsCancellationRequested) return;
                    _longPressFired = true;
                    _waitingForDoublePress = false;
                    _callback(_buttonIndex, PressType.Long);
                    _longPressCts?.Dispose();
                    _longPressCts = null;
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task SinglePressTimerAsync(CancellationToken ct, int doublePressWindowMs)
        {
            try
            {
                await Task.Delay(doublePressWindowMs, ct);
                lock (_lock)
                {
                    if (ct.IsCancellationRequested) return;
                    _waitingForDoublePress = false;
                    _callback(_buttonIndex, PressType.Single);
                    _singlePressCts?.Dispose();
                    _singlePressCts = null;
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
