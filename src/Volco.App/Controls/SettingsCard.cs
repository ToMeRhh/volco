using Avalonia;
using Avalonia.Controls;

namespace Volco.App.Controls;

/// <summary>
/// A settings row: optional icon, header, optional description, and a content slot on the right.
/// Derives from <see cref="ContentControl"/> (itself a TemplatedControl) for the content slot.
/// Template lives in SettingsCard.axaml.
/// </summary>
public class SettingsCard : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Description));

    public static readonly StyledProperty<object?> HeaderIconProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(HeaderIcon));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }
}
