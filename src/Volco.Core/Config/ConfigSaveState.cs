namespace Volco.Core.Config;

/// <summary>Whether the latest config changes are on disk (<see cref="ConfigManager.SaveState"/>).</summary>
public enum ConfigSaveState
{
    /// <summary>Everything is written, or the latest change is still within its short debounce.</summary>
    Saved,
    /// <summary>A change has waited a second or more to be written (an edit still being typed, or a
    /// first write that failed and will be retried).</summary>
    Unsaved,
    /// <summary>Two writes in a row failed; Volco keeps retrying (<see cref="ConfigManager.SaveError"/> says why).</summary>
    Failed,
}
