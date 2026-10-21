using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Inspector;

/// <summary>
/// The collapsible Hardware section under a tile's Controls inspector (controller-page spec §4.3).
/// The inspector binds what it shows: whether it is open (remembered per kind by
/// <see cref="Volco.App.ViewModels.Hardware.InspectorSections"/>), the summary, the danger mark,
/// the control's wiring view model, and whether the controller lacks the control.
/// </summary>
public partial class HardwareSection : UserControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<HardwareSection, string?>(nameof(Summary));

    public static readonly StyledProperty<bool> HasProblemProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(HasProblem));

    public static readonly StyledProperty<object?> WiringProperty =
        AvaloniaProperty.Register<HardwareSection, object?>(nameof(Wiring));

    public static readonly StyledProperty<bool> IsMissingProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(IsMissing));

    public HardwareSection()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The section's body shows.</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>The header's summary ("GPIO 33", "GPIO 5 · LED 12").</summary>
    public string? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    /// <summary>The draft has a problem with the control: the header's danger mark.</summary>
    public bool HasProblem
    {
        get => GetValue(HasProblemProperty);
        set => SetValue(HasProblemProperty, value);
    }

    /// <summary>The control's wiring view model (a slider's or a button's), the section's body.</summary>
    public object? Wiring
    {
        get => GetValue(WiringProperty);
        set => SetValue(WiringProperty, value);
    }

    /// <summary>The controller lacks the control: the body says "Not on this controller".</summary>
    public bool IsMissing
    {
        get => GetValue(IsMissingProperty);
        set => SetValue(IsMissingProperty, value);
    }
}
