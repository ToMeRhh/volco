using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public class NextPresetAction : IButtonAction
{
    private readonly IPresetController _presets;

    public NextPresetAction(IPresetController presets) => _presets = presets;

    public void Execute() => _presets.NextPreset();
}
