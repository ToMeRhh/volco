using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Volco.App.Controls;

/// <summary>
/// A line of text that describes a state, led by an icon in the state's colour: a check (Good), a
/// triangle (Warning), a cross (Danger) or an "i" (Neutral). Danger and Warning colour the text
/// too, so the meaning never rests on colour alone. Classes "Subtitle" and "Caption" pick the
/// type size. Template in StatusLine.axaml.
/// </summary>
public class StatusLine : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusLine, string?>(nameof(Text));

    public static readonly StyledProperty<LinkTone> ToneProperty =
        AvaloniaProperty.Register<StatusLine, LinkTone>(nameof(Tone));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public LinkTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public StatusLine() => UpdatePseudoClasses(Tone);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty) UpdatePseudoClasses(change.GetNewValue<LinkTone>());
    }

    private void UpdatePseudoClasses(LinkTone tone)
    {
        PseudoClasses.Set(":good", tone == LinkTone.Good);
        PseudoClasses.Set(":warning", tone == LinkTone.Warning);
        PseudoClasses.Set(":danger", tone == LinkTone.Danger);
        PseudoClasses.Set(":neutral", tone == LinkTone.Neutral);
    }
}
