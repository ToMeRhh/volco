namespace Volco.App.ViewModels.Hardware;

/// <summary>How much the controller's status needs the user (controller-page spec §5), and so
/// where it shows: <see cref="DeviceCard.LevelOf"/>.</summary>
public enum StatusLevel
{
    /// <summary>Connected and in sync, or an expected wait: only the quiet line in the sidebar.</summary>
    Fine,
    /// <summary>The user has something to do: a banner across the top of every page.</summary>
    NeedsYou,
    /// <summary>A firmware operation or a save runs: don't interrupt it.</summary>
    Running,
}
