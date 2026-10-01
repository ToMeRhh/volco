using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public class GoToPresetAction : IButtonAction
{
    private readonly IPresetController _presets;
    private readonly string? _presetId;

    public GoToPresetAction(IPresetController presets, string? presetId)
    {
        _presets = presets;
        _presetId = presetId;
    }

    /// <summary>No-op when no preset was chosen; an unknown id is ignored by the controller.</summary>
    public void Execute()
    {
        if (!string.IsNullOrEmpty(_presetId)) _presets.SetActivePreset(_presetId);
    }
}
