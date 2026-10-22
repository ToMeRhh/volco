using Avalonia.Controls;

namespace Volco.App.Views.Inspector;

/// <summary>
/// A tile's inspector (controller-page spec §4.3), in the side panel's one scrolling card: the
/// header and the Controls or Actions section ("TopContent", inside "Top") over the Hardware
/// section. "Top" is at least the page's <see cref="Stage.StagePage.InspectorTopHeight"/> tall, so
/// the Hardware section starts at the same height for every tile. A probe (<see cref="IsProbe"/>)
/// is an unseen copy the page measures to find that height; it has no Hardware section.
/// </summary>
public abstract class TileInspector : UserControl
{
    private bool _isProbe;

    /// <summary>The sub-section's body (Response, Timing), left out of <see cref="ClosedTopHeight"/>.</summary>
    protected abstract string SubSectionBodyName { get; }

    /// <summary>An unseen copy the page measures (set once, in XAML): the Hardware section goes.</summary>
    public bool IsProbe
    {
        get => _isProbe;
        set
        {
            if (_isProbe || !value) return;
            _isProbe = true;
            if (this.FindControl<Control>("HardwareSection") is { Parent: Panel panel } section)
                panel.Children.Remove(section);
        }
    }

    /// <summary>The header and the Controls or Actions section, with the sub-section closed: the
    /// room this tile needs above its Hardware section.</summary>
    internal double ClosedTopHeight
    {
        get
        {
            if (this.FindControl<Control>("TopContent") is not { } top) return 0;
            var body = this.FindControl<Control>(SubSectionBodyName);
            return top.DesiredSize.Height - (body is { IsEffectivelyVisible: true } ? body.DesiredSize.Height : 0);
        }
    }
}
