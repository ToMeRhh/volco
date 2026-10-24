using Avalonia.Controls;
using Avalonia.Data;

namespace Volco.App.Views.Inspector;

/// <summary>
/// A tile's inspector (controller-page spec §4.3), in the side panel's one scrolling card: the
/// header and the Controls or Actions section ("TopContent", inside "Top") over the Hardware
/// section. "Top" is at least the page's <see cref="Stage.StagePage.InspectorTopHeight"/> tall, so
/// the Hardware section starts at the same height for every tile, below even an open Timing. A probe (<see cref="IsProbe"/>) is an unseen copy the page
/// measures to find that height: its sub-section is always open and it has no Hardware section.
/// </summary>
public abstract class TileInspector : UserControl
{
    private bool _isProbe;

    /// <summary>The sub-section's body (Timing), which a probe always shows; null for a tile with none.</summary>
    protected virtual string? SubSectionBodyName => null;

    /// <summary>An unseen copy the page measures (set once, in XAML): the Hardware section goes,
    /// and the sub-section shows whether or not the tile's is open.</summary>
    public bool IsProbe
    {
        get => _isProbe;
        set
        {
            if (_isProbe || !value) return;
            _isProbe = true;
            if (this.FindControl<Control>("HardwareSection") is { Parent: Panel panel } section)
                panel.Children.Remove(section);
            // Above the IsAdvancedOpen binding, which the probe shares with the real inspector
            if (SubSectionBodyName is { } body)
                this.FindControl<Control>(body)?.SetValue(IsVisibleProperty, true, BindingPriority.Animation);
        }
    }

    /// <summary>The header and the Controls or Actions section with the sub-section open: the
    /// room this tile needs above its Hardware section.</summary>
    internal double TopHeight => this.FindControl<Control>("TopContent")?.DesiredSize.Height ?? 0;
}
