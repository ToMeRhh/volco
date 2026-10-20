using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Volco.App.Views.Stage;

/// <summary>
/// The stage's crossfades (spec §3.3): what changes between Controls and Hardware fades in
/// <see cref="Duration"/>; nothing slides. With <see cref="Enabled"/> false (tests, or animations
/// turned off) it swaps at once.
/// </summary>
public static class StageTransitions
{
    /// <summary>Whether the stage animates; read each time the mode changes.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>How long a crossfade takes.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    /// <summary>The style classes of the two layers in each tile and in the mode frame: the first is
    /// shown on Controls, the second on Hardware (styles in VolcoStyles.axaml).</summary>
    public const string ControlsLayer = "ControlsLayer";
    public const string WiringLayer = "WiringLayer";

    /// <summary>The crossfade for a <see cref="TransitioningContentControl"/>, or none.</summary>
    internal static IPageTransition? PageTransition() => Enabled ? new CrossFade(Duration) : null;

    /// <summary>Gives every layer (and each tile's problem outline) under <paramref name="root"/> its opacity transition (or takes it
    /// away when <see cref="Enabled"/> is false). Called just before a mode change, so a tile
    /// never fades in when it is first shown.</summary>
    internal static void Prepare(Visual root)
    {
        foreach (var layer in root.GetVisualDescendants().OfType<Control>())
        {
            if (!layer.Classes.Contains(ControlsLayer) && !layer.Classes.Contains(WiringLayer)
                && !layer.Classes.Contains("ProblemOutline")) continue;
            if (!Enabled) layer.Transitions = null;
            else if (layer.Transitions is not { Count: > 0 })
                layer.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration } };
        }
    }
}
