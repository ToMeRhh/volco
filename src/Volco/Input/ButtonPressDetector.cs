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
    private readonly Dictionary<int, ButtonStateTracker> _trackers = new();

    public event EventHandler<ButtonPressEventArgs>? PressDetected;

    public ButtonPressDetector(int longPressMs = 500, int doublePressWindowMs = 300)
    {
        _longPressMs = longPressMs;
        _doublePressWindowMs = doublePressWindowMs;
    }

    public void HandleButtonEvent(int buttonIndex, ButtonState state)
    {
        if (!_trackers.TryGetValue(buttonIndex, out var tracker))
        {
            tracker = new ButtonStateTracker(buttonIndex, _longPressMs, _doublePressWindowMs, OnPressDetected);
            _trackers[buttonIndex] = tracker;
        }

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
            if (state == ButtonState.Pressed)
                OnPressed();
            else
                OnReleased();
        }

        private void OnPressed()
        {
            _longPressFired = false;
            _longPressCts?.Cancel();
            _singlePressCts?.Cancel();
            _longPressCts = new CancellationTokenSource();
            _ = LongPressTimerAsync(_longPressCts.Token);
        }

        private void OnReleased()
        {
            _longPressCts?.Cancel();

            if (_longPressFired)
            {
                _longPressFired = false;
                return;
            }

            if (_waitingForDoublePress)
            {
                _singlePressCts?.Cancel();
                _waitingForDoublePress = false;
                _callback(_buttonIndex, PressType.Double);
                return;
            }

            _waitingForDoublePress = true;
            _singlePressCts?.Cancel();
            _singlePressCts = new CancellationTokenSource();
            _ = SinglePressTimerAsync(_singlePressCts.Token);
        }

        private async Task LongPressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_longPressMs, ct);
                _longPressFired = true;
                _callback(_buttonIndex, PressType.Long);
            }
            catch (OperationCanceledException) { }
        }

        private async Task SinglePressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_doublePressWindowMs, ct);
                _waitingForDoublePress = false;
                _callback(_buttonIndex, PressType.Single);
            }
            catch (OperationCanceledException) { }
        }
    }
}
