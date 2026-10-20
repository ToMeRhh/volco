using Volco.Core.Config;

namespace Volco.App;

/// <summary>
/// Every word the UI says about saving the settings file, so the sidebar footer and the tray agree.
/// The two states look different on purpose: unsaved changes are routine (grey, "Unsaved changes",
/// saved in a moment); a failed save is a problem (red, "Settings not saved", with the reason and Retry).
/// </summary>
public static class SaveText
{
    public static string Footer(ConfigSaveState state) => state switch
    {
        ConfigSaveState.Unsaved => "Unsaved changes",
        ConfigSaveState.Failed => "Settings not saved",
        _ => "",
    };

    public static LinkTone Tone(ConfigSaveState state)
        => state == ConfigSaveState.Failed ? LinkTone.Danger : LinkTone.Neutral;

    public static string ToolTip(ConfigSaveState state, string? error) => state switch
    {
        ConfigSaveState.Unsaved => "Your latest changes aren't in the settings file yet. Volco saves them in a moment.",
        ConfigSaveState.Failed => $"Volco can't write its settings file{(string.IsNullOrWhiteSpace(error) ? "." : $": {error}")}\n"
                                  + "It keeps trying. Changes made since the last save are lost if Volco exits before one works.",
        _ => "",
    };

    /// <summary>The tray tooltip's extra line; empty when everything is saved.</summary>
    public static string TrayLine(ConfigSaveState state) => state switch
    {
        ConfigSaveState.Unsaved => "Unsaved changes",
        ConfigSaveState.Failed => "Settings not saved: open Volco for details",
        _ => "",
    };
}
