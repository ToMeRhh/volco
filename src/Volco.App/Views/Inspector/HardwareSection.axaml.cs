using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Volco.App.Views.Inspector;

/// <summary>
/// The Hardware section under a tile's Controls (or Actions) section (controller-page spec §4.3):
/// always shown, the bottom third of the inspector's sections grid, never less than its header
/// and <see cref="MinRowsHeight"/> of rows (the Controls section gives way first). The inspector
/// binds what it shows: the danger mark, the control's wiring view model, and whether the
/// controller lacks the control. Hidden (a stage without the controller draft), its row takes
/// no room.
/// </summary>
public partial class HardwareSection : UserControl
{
    /// <summary>The fixed header's height.</summary>
    public const double HeaderHeight = 36;

    /// <summary>The least room the rows get: about four rows.</summary>
    public const double MinRowsHeight = 176;

    public static readonly StyledProperty<bool> HasProblemProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(HasProblem));

    public static readonly StyledProperty<object?> WiringProperty =
        AvaloniaProperty.Register<HardwareSection, object?>(nameof(Wiring));

    public static readonly StyledProperty<bool> IsMissingProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(IsMissing));

    public static readonly StyledProperty<bool> CanRemoveMissingProperty =
        AvaloniaProperty.Register<HardwareSection, bool>(nameof(CanRemoveMissing));

    public static readonly StyledProperty<ICommand?> RemoveMissingCommandProperty =
        AvaloniaProperty.Register<HardwareSection, ICommand?>(nameof(RemoveMissingCommand));

    public HardwareSection()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SizeRow();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) SizeRow();
    }

    /// <summary>Gives the section's row of the inspector's grid its third and its minimum, or
    /// nothing while the section is hidden.</summary>
    private void SizeRow()
    {
        if (Parent is not Grid grid) return;
        var row = Grid.GetRow(this);
        if (row >= grid.RowDefinitions.Count) return;
        var definition = grid.RowDefinitions[row];
        definition.Height = IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        definition.MinHeight = IsVisible ? HeaderHeight + MinRowsHeight : 0;
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

    /// <summary>The control the controller lacks is the last one: "Not on this controller" has
    /// Controls' Remove (input number is position, so only the last can go).</summary>
    public bool CanRemoveMissing
    {
        get => GetValue(CanRemoveMissingProperty);
        set => SetValue(CanRemoveMissingProperty, value);
    }

    /// <summary>Controls' remove of the last control (Volco's settings only, with Undo).</summary>
    public ICommand? RemoveMissingCommand
    {
        get => GetValue(RemoveMissingCommandProperty);
        set => SetValue(RemoveMissingCommandProperty, value);
    }
}
