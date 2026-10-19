using Volco.Core.Runtime;

namespace Volco.Core.Input.Actions;

public class NextModeAction : IButtonAction
{
    private readonly IModeController _modes;

    public NextModeAction(IModeController modes) => _modes = modes;

    public void Execute() => _modes.NextMode();
}
