using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Hardware section of a button's inspector (controller-page spec §4.3, hardware-page spec
/// §6.2): its pin, its light (None / LED / 2 LEDs / Screen, which reshapes only the rows under it)
/// and Remove, with the collapsed section's summary. Every edit goes into the draft of one button;
/// the Hardware view model refreshes it after every draft change and drops it when the selection moves.
/// </summary>
public sealed partial class ButtonWiringViewModel : ObservableObject
{
    private readonly ControllerViewModel _controller;

    internal ButtonWiringViewModel(int index, ButtonItemViewModel item, ControllerViewModel controller,
                                   Func<PinOwnerKind, PinFieldViewModel> pin, Action remove)
    {
        Index = index;
        Item = item;
        _controller = controller;
        Pin = pin(PinOwnerKind.Button);
        LightPin = pin(PinOwnerKind.Light);
        LightAPin = pin(PinOwnerKind.LightA);
        LightBPin = pin(PinOwnerKind.LightB);
        RemoveCommand = new RelayCommand(remove);
    }

    /// <summary>The draft button (0-based).</summary>
    public int Index { get; }

    /// <summary>The Controls item it draws.</summary>
    public ButtonItemViewModel Item { get; }

    /// <summary>"Button N".</summary>
    public string Title => $"Button {Index + 1}";

    /// <summary>The collapsed section's summary: the pin and the light's wiring ("GPIO 5 · LED 12").</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>The draft has a problem with this button: the section's danger mark.</summary>
    [ObservableProperty] private bool _hasProblem;

    public PinFieldViewModel Pin { get; }
    public PinFieldViewModel LightPin { get; }
    public PinFieldViewModel LightAPin { get; }
    public PinFieldViewModel LightBPin { get; }

    /// <summary>"Remove Button N".</summary>
    public string RemoveText => $"Remove {Title}";

    /// <summary>Asks "Remove Button N?", then removes it from the draft.</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>The draft can be edited: the light's choices and Remove are enabled.</summary>
    [ObservableProperty] private bool _isEditable;

    public static IReadOnlyList<string> BusChoices { get; } = new[] { "0", "1", "2 (software)", "3 (software)" };
    public static IReadOnlyList<string> AddressChoices { get; } = new[] { "0x3C", "0x3D" };

    private Button? Message => Index < _controller.Draft.Buttons.Count ? _controller.Draft.Buttons[Index] : null;

    /// <summary>0 None, 1 LED, 2 two LEDs, 3 Screen. A change starts the new light empty.</summary>
    public int LightIndex
    {
        get => Message?.IndicatorCase switch
        {
            Button.IndicatorOneofCase.Led => 1,
            Button.IndicatorOneofCase.LedPair => 2,
            Button.IndicatorOneofCase.Screen => 3,
            _ => 0,
        };
        set
        {
            if (Message is not { } b || value is < 0 or > 3 || value == LightIndex || !_controller.CanEdit) { OnPropertyChanged(); return; }
            switch (value)
            {
                case 1: b.Led = new Led(); break;
                case 2: b.LedPair = new LedPair(); break;
                case 3: b.Screen = new Screen(); break;
                default: b.ClearIndicator(); break;
            }
            Refresh();
            _controller.OnDraftEdited();
        }
    }

    public bool IsNone { get => LightIndex == 0; set { if (value) LightIndex = 0; } }
    public bool IsLed { get => LightIndex == 1; set { if (value) LightIndex = 1; } }
    public bool IsPair { get => LightIndex == 2; set { if (value) LightIndex = 2; } }
    public bool IsScreen { get => LightIndex == 3; set { if (value) LightIndex = 3; } }

    /// <summary>An LED or two: the "Lit when pin is" row.</summary>
    public bool HasLeds => LightIndex is 1 or 2;

    /// <summary>"Lit when pin is" High (true) or Low.</summary>
    public bool LitWhenHigh
    {
        get => Message switch
        {
            { IndicatorCase: Button.IndicatorOneofCase.Led } b => !b.Led.ActiveLow,
            { IndicatorCase: Button.IndicatorOneofCase.LedPair } b => !b.LedPair.ActiveLow,
            _ => true,
        };
        set
        {
            if (Message is not { } b || value == LitWhenHigh || !_controller.CanEdit) { OnLitChanged(); return; }
            if (b.IndicatorCase == Button.IndicatorOneofCase.Led) b.Led.ActiveLow = !value;
            else if (b.IndicatorCase == Button.IndicatorOneofCase.LedPair) b.LedPair.ActiveLow = !value;
            else { OnLitChanged(); return; }
            OnLitChanged();
            _controller.OnDraftEdited();
        }
    }

    public bool LitWhenLow { get => !LitWhenHigh; set { if (value) LitWhenHigh = false; } }

    /// <summary>The screen's bus, 0-3.</summary>
    public int BusIndex
    {
        get => Message is { IndicatorCase: Button.IndicatorOneofCase.Screen } b && b.Screen.Bus <= 3 ? (int)b.Screen.Bus : 0;
        set
        {
            if (Message is not { IndicatorCase: Button.IndicatorOneofCase.Screen } b || value is < 0 or > 3
                || value == BusIndex || !_controller.CanEdit) { OnPropertyChanged(); return; }
            b.Screen.Bus = (uint)value;
            OnPropertyChanged();
            _controller.OnDraftEdited();
        }
    }

    /// <summary>0 = 0x3C, 1 = 0x3D.</summary>
    public int AddressIndex
    {
        get => Message is { IndicatorCase: Button.IndicatorOneofCase.Screen, Screen.Address: 0x3D } ? 1 : 0;
        set
        {
            if (Message is not { IndicatorCase: Button.IndicatorOneofCase.Screen } b || value is < 0 or > 1
                || value == AddressIndex || !_controller.CanEdit) { OnPropertyChanged(); return; }
            b.Screen.Address = value == 1 ? 0x3Du : 0x3Cu;
            OnPropertyChanged();
            _controller.OnDraftEdited();
        }
    }

    private void OnLitChanged()
    {
        OnPropertyChanged(nameof(LitWhenHigh));
        OnPropertyChanged(nameof(LitWhenLow));
    }

    /// <summary>The draft changed: re-reads every value.</summary>
    internal void Refresh()
    {
        IsEditable = _controller.CanEdit;
        foreach (var name in new[] { nameof(LightIndex), nameof(IsNone), nameof(IsLed), nameof(IsPair), nameof(IsScreen),
                                     nameof(HasLeds), nameof(LitWhenHigh), nameof(LitWhenLow), nameof(BusIndex), nameof(AddressIndex) })
            OnPropertyChanged(name);
    }
}
