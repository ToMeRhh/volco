using System.Collections.Concurrent;
using Volco.Communication;

namespace Volco.Input;

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
    {
        var tracker = _trackers.GetOrAdd(buttonIndex, index =>
            new ButtonStateTracker(index, _longPressMs, _doublePressWindowMs, OnPressDetected));

        tracker.HandleState(state);
    }

    private void OnPressDetected(int buttonIndex, PressType pressType)
    {
        PressDetected?.Invoke(this, new ButtonPressEventArgs(buttonIndex, pressType));
    }

    private class ButtonStateTracker
    {
        private readonly int _buttonIndex;
        private readonly int _longPressMs;
        private readonly int _doublePressWindowMs;
        private readonly Action<int, PressType> _callback;
        private readonly object _lock = new();
        private CancellationTokenSource? _longPressCts;
        private CancellationTokenSource? _singlePressCts;
        private bool _longPressFired;
        private bool _waitingForDoublePress;

        public ButtonStateTracker(int buttonIndex, int longPressMs, int doublePressWindowMs, Action<int, PressType> callback)
        {
            _buttonIndex = buttonIndex;
            _longPressMs = longPressMs;
            _doublePressWindowMs = doublePressWindowMs;
            _callback = callback;
        }

        public void HandleState(ButtonState state)
        {
            lock (_lock)
            {
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
            _ = LongPressTimerAsync(_longPressCts.Token);
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

            _waitingForDoublePress = true;
            _singlePressCts?.Cancel();
            _singlePressCts?.Dispose();
            _singlePressCts = new CancellationTokenSource();
            _ = SinglePressTimerAsync(_singlePressCts.Token);
        }

        private async Task LongPressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_longPressMs, ct);
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

        private async Task SinglePressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_doublePressWindowMs, ct);
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
