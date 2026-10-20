using Volco.Core.Config;

namespace Volco.Core.Controller;

/// <summary>Which saved slider or button each control of a controller draft came from. Removing a
/// middle control moves the later ones down with what they control; an added control is new.
/// <see cref="ApplyTo"/> rewrites Volco's config lists to match after a confirmed save.</summary>
public sealed class ControlLayout
{
    private readonly List<int?> _sliders;
    private readonly List<int?> _buttons;
    private readonly int _savedSliders;
    private readonly int _savedButtons;

    private ControlLayout(int sliders, int buttons)
    {
        _savedSliders = sliders;
        _savedButtons = buttons;
        _sliders = Enumerable.Range(0, sliders).Select(i => (int?)i).ToList();
        _buttons = Enumerable.Range(0, buttons).Select(i => (int?)i).ToList();
    }

    /// <summary>A layout where every draft control is the saved one at its position.</summary>
    public static ControlLayout Identity(int sliders, int buttons) => new(sliders, buttons);

    /// <summary>For each draft slider, the saved index it came from, or null for an added one.</summary>
    public IReadOnlyList<int?> Sliders => _sliders;

    /// <summary>For each draft button, the saved index it came from, or null for an added one.</summary>
    public IReadOnlyList<int?> Buttons => _buttons;

    /// <summary>No removals, and nothing added beyond the saved count.</summary>
    public bool IsIdentity => Matches(_sliders, _savedSliders) && Matches(_buttons, _savedButtons);

    public void RemoveSlider(int draftIndex) => _sliders.RemoveAt(draftIndex);
    public void AddSlider() => _sliders.Add(null);
    public void RemoveButton(int draftIndex) => _buttons.RemoveAt(draftIndex);
    public void AddButton() => _buttons.Add(null);

    /// <summary>Rebuilds <c>config.Sliders</c> and <c>config.Buttons</c> from the sources: existing
    /// entries are moved, added ones are empty, saved ones past the draft are dropped, and each
    /// <c>Index</c> is rewritten to its position.</summary>
    public void ApplyTo(AppConfig config)
    {
        var sliders = config.Sliders.ToList();
        var buttons = config.Buttons.ToList();
        config.Sliders = _sliders
            .Select((src, i) => Pick(sliders, src) ?? new SliderConfig { Type = SliderMappingType.None })
            .ToList();
        config.Buttons = _buttons
            .Select((src, i) => Pick(buttons, src) ?? new ButtonConfig())
            .ToList();
        for (var i = 0; i < config.Sliders.Count; i++) config.Sliders[i].Index = i;
        for (var i = 0; i < config.Buttons.Count; i++) config.Buttons[i].Index = i;
    }

    private static T? Pick<T>(List<T> saved, int? source) where T : class
        => source is int s && s < saved.Count ? saved[s] : null;

    private static bool Matches(List<int?> sources, int saved)
    {
        if (sources.Count != saved) return false;
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] != i) return false;
        return true;
    }
}
