using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels;

/// <summary>Base of a hardware row: it edits one message inside the page's draft and tells the
/// page after every change. Pins are <c>decimal?</c> for NumericUpDown; empty = absent.</summary>
public abstract partial class HardwareRowViewModel : ObservableObject
{
    protected HardwareRowViewModel(ControllerViewModel owner, int index)
    {
        Owner = owner;
        Index = index;
    }

    protected ControllerViewModel Owner { get; }
    public int Index { get; }
    public bool CanEdit => Owner.CanEdit;

    [ObservableProperty] private string? _hintText;

    public bool HasHint => HintText is not null;
    partial void OnHintTextChanged(string? value) => OnPropertyChanged(nameof(HasHint));

    private readonly List<(ConfigField Field, string Message)> _errors = new();

    /// <summary>Every problem on the row, one per line; null when there is none.</summary>
    public string? ErrorText => _errors.Count == 0 ? null : string.Join("\n", _errors.Select(e => e.Message));
    public bool HasError => _errors.Count > 0;

    /// <summary>A problem is on <paramref name="field"/>: its box is marked.</summary>
    public bool IsInvalid(ConfigField field) => _errors.Any(e => e.Field == field);

    internal void ClearErrors()
    {
        if (_errors.Count == 0) return;
        _errors.Clear();
        OnPropertyChanged(string.Empty);
    }

    internal void AddError(ConfigField field, string message)
    {
        if (_errors.Contains((field, message))) return;
        _errors.Add((field, message));
        OnPropertyChanged(string.Empty);
    }

    internal void RefreshCanEdit() => OnPropertyChanged(nameof(CanEdit));

    protected static decimal? Get(bool has, uint value) => has ? value : null;

    /// <summary>Applies a pin edit: a value sets it, empty clears it.</summary>
    protected void Edit(decimal? value, Action<uint> set, Action clear, string property)
    {
        if (!CanEdit) { OnPropertyChanged(property); return; }
        if (value is { } v && v >= 0) set((uint)v); else clear();
        OnPropertyChanged(property);
        Owner.OnDraftEdited();
    }
}

public sealed partial class HwSliderRowViewModel : HardwareRowViewModel
{
    private readonly Slider _slider;

    public HwSliderRowViewModel(ControllerViewModel owner, int index, Slider slider) : base(owner, index)
    {
        _slider = slider;
        RemoveCommand = new RelayCommand(() => owner.RequestRemoveSlider(Index));
    }

    public string Title => $"Slider {Index + 1}";
    public IRelayCommand RemoveCommand { get; }
    public bool PinInvalid => IsInvalid(ConfigField.SliderPin);

    public decimal? Pin
    {
        get => Get(_slider.HasPin, _slider.Pin);
        set => Edit(value, v => _slider.Pin = v, _slider.ClearPin, nameof(Pin));
    }
}

public sealed partial class HwButtonRowViewModel : HardwareRowViewModel
{
    private readonly Button _button;

    public HwButtonRowViewModel(ControllerViewModel owner, int index, Button button) : base(owner, index)
    {
        _button = button;
        RemoveCommand = new RelayCommand(() => owner.RequestRemoveButton(Index));
    }

    public string Title => $"Button {Index + 1}";
    public IRelayCommand RemoveCommand { get; }
    public bool PinInvalid => IsInvalid(ConfigField.ButtonPin);
    public bool LedPinInvalid => IsInvalid(ConfigField.LedPin);
    public bool PairPinAInvalid => IsInvalid(ConfigField.PairPinA);
    public bool PairPinBInvalid => IsInvalid(ConfigField.PairPinB);
    public bool ScreenBusInvalid => IsInvalid(ConfigField.ScreenBus);
    public bool ScreenAddressInvalid => IsInvalid(ConfigField.ScreenAddress);

    public static IReadOnlyList<string> IndicatorChoices { get; } = new[] { "None", "LED", "LED pair", "Screen" };
    public static IReadOnlyList<string> PolarityChoices { get; } = new[] { "High", "Low" };
    public static IReadOnlyList<string> BusChoices { get; } = new[] { "Bus 0", "Bus 1", "Bus 2 (software)", "Bus 3 (software)" };
    public static IReadOnlyList<string> AddressChoices { get; } = new[] { "0x3C", "0x3D" };

    public decimal? Pin
    {
        get => Get(_button.HasPin, _button.Pin);
        set => Edit(value, v => _button.Pin = v, _button.ClearPin, nameof(Pin));
    }

    /// <summary>0 none, 1 LED, 2 LED pair, 3 screen.</summary>
    public int IndicatorIndex
    {
        get => _button.IndicatorCase switch
        {
            Button.IndicatorOneofCase.Led => 1,
            Button.IndicatorOneofCase.LedPair => 2,
            Button.IndicatorOneofCase.Screen => 3,
            _ => 0,
        };
        set
        {
            if (value == IndicatorIndex || !CanEdit) { OnPropertyChanged(); return; }
            switch (value)
            {
                case 1: _button.Led = new Led(); break;
                case 2: _button.LedPair = new LedPair(); break;
                case 3: _button.Screen = new Screen(); break;
                default: _button.ClearIndicator(); break;
            }
            OnPropertyChanged(string.Empty);   // every indicator field
            Owner.OnDraftEdited(structural: true);   // the I2C rows and Behaviour depend on screens
        }
    }

    public bool IsLed => IndicatorIndex == 1;
    public bool IsPair => IndicatorIndex == 2;
    public bool IsScreen => IndicatorIndex == 3;

    public decimal? LedPin
    {
        get => _button.Led is { } l ? Get(l.HasPin, l.Pin) : null;
        set { if (_button.Led is { } l) Edit(value, v => l.Pin = v, l.ClearPin, nameof(LedPin)); }
    }

    /// <summary>"Lights when the pin is": 0 High, 1 Low.</summary>
    public int LedPolarityIndex
    {
        get => _button.Led is { ActiveLow: true } ? 1 : 0;
        set { if (_button.Led is { } l && CanEdit) { l.ActiveLow = value == 1; Owner.OnDraftEdited(); } OnPropertyChanged(); }
    }

    public decimal? PairPinA
    {
        get => _button.LedPair is { } p ? Get(p.HasPinA, p.PinA) : null;
        set { if (_button.LedPair is { } p) Edit(value, v => p.PinA = v, p.ClearPinA, nameof(PairPinA)); }
    }

    public decimal? PairPinB
    {
        get => _button.LedPair is { } p ? Get(p.HasPinB, p.PinB) : null;
        set { if (_button.LedPair is { } p) Edit(value, v => p.PinB = v, p.ClearPinB, nameof(PairPinB)); }
    }

    public int PairPolarityIndex
    {
        get => _button.LedPair is { ActiveLow: true } ? 1 : 0;
        set { if (_button.LedPair is { } p && CanEdit) { p.ActiveLow = value == 1; Owner.OnDraftEdited(); } OnPropertyChanged(); }
    }

    public int ScreenBusIndex
    {
        get => _button.Screen is { } s && s.Bus <= 3 ? (int)s.Bus : 0;
        set
        {
            if (_button.Screen is not { } s || value < 0 || value > 3 || value == ScreenBusIndex || !CanEdit) { OnPropertyChanged(); return; }
            s.Bus = (uint)value;
            OnPropertyChanged();
            Owner.OnDraftEdited(structural: true);
        }
    }

    public int ScreenAddressIndex
    {
        get => _button.Screen is { Address: 0x3D } ? 1 : 0;
        set { if (_button.Screen is { } s && CanEdit) { s.Address = value == 1 ? 0x3Du : 0x3Cu; Owner.OnDraftEdited(); } OnPropertyChanged(); }
    }
}

public sealed partial class I2cBusRowViewModel : HardwareRowViewModel
{
    private readonly ControllerViewModel _owner;

    public I2cBusRowViewModel(ControllerViewModel owner, int bus) : base(owner, bus) => _owner = owner;

    public string Title => Index >= 2 ? $"I2C bus {Index} (software)" : $"I2C bus {Index}";
    public bool PinsInvalid => IsInvalid(ConfigField.I2cPins) || IsInvalid(ConfigField.I2cMissing);
    public bool ClockInvalid => IsInvalid(ConfigField.I2cClock);

    private I2cBus? Message => ControllerDraft.Bus(_owner.Draft, Index);

    private I2cBus Ensure()
    {
        var m = Message;
        if (m is null) ControllerDraft.SetBus(_owner.Draft, Index, m = new I2cBus());
        return m;
    }

    public decimal? Sda
    {
        get => Message is { } m ? Get(m.HasSda, m.Sda) : null;
        set { if (!CanEdit) { OnPropertyChanged(); return; } var m = Ensure(); Edit(value, v => m.Sda = v, m.ClearSda, nameof(Sda)); }
    }

    public decimal? Scl
    {
        get => Message is { } m ? Get(m.HasScl, m.Scl) : null;
        set { if (!CanEdit) { OnPropertyChanged(); return; } var m = Ensure(); Edit(value, v => m.Scl = v, m.ClearScl, nameof(Scl)); }
    }

    /// <summary>The clock in kHz (100–1000).</summary>
    public decimal? ClockKhz
    {
        get => (Message?.ClockHz ?? 400_000u) / 1000m;
        set
        {
            if (!CanEdit) { OnPropertyChanged(); return; }
            var m = Ensure();
            if (value is { } v) m.ClockHz = (uint)(v * 1000); else m.ClearClockHz();
            OnPropertyChanged();
            Owner.OnDraftEdited();
        }
    }
}
