using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Volco.App.Controls;

/// <summary>
/// A small "?" beside a setting's label whose tooltip, shown on hover, explains the setting
/// (<see cref="Text"/>). Only for settings whose label doesn't say enough on its own.
/// </summary>
public sealed class HelpTip : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HelpTip, string?>(nameof(Text));

    private readonly TextBlock _tip = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };

    public HelpTip()
    {
        Child = new TextBlock
        {
            Text = "?",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(this, _tip);
        ToolTip.SetShowDelay(this, 150);
        ToolTip.SetBetweenShowDelay(this, 0);
        ToolTip.SetPlacement(this, PlacementMode.Bottom);
    }

    /// <summary>The explanation the tooltip shows.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        _tip.Text = Text;
        AutomationProperties.SetHelpText(this, Text);
    }
}
