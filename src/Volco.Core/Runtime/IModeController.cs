namespace Volco.Core.Runtime;

/// <summary>
/// Owns which button mode is live. <see cref="VolcoRuntime"/> implements it; the mode
/// actions (<see cref="Input.Actions.NextModeAction"/>, <see cref="Input.Actions.GoToModeAction"/>)
/// and the settings UI drive it.
/// </summary>
public interface IModeController
{
    /// <summary>The <see cref="Config.ModeConfig.Id"/> of the live mode.</summary>
    string ActiveModeId { get; }

    /// <summary>Makes <paramref name="modeId"/> live. An id that names no mode is ignored.</summary>
    void SetActiveMode(string modeId);

    /// <summary>Makes the next mode in tab order live, wrapping to the first.</summary>
    void NextMode();
}
