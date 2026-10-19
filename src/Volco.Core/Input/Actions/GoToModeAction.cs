using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public class GoToModeAction : IButtonAction
{
    private readonly IModeController _modes;
    private readonly string? _modeId;

    public GoToModeAction(IModeController modes, string? modeId)
    {
        _modes = modes;
        _modeId = modeId;
    }

    /// <summary>No-op when no mode was chosen; an unknown id is ignored by the controller.</summary>
    public void Execute()
    {
        if (!string.IsNullOrEmpty(_modeId)) _modes.SetActiveMode(_modeId);
    }
}
