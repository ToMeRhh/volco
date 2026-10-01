namespace Volco.Core.Runtime;

/// <summary>
/// Owns which button preset is live. <see cref="VolcoRuntime"/> implements it; the preset
/// actions (<see cref="Input.Actions.NextPresetAction"/>, <see cref="Input.Actions.GoToPresetAction"/>)
/// and the settings UI drive it.
/// </summary>
public interface IPresetController
{
    /// <summary>The <see cref="Config.PresetConfig.Id"/> of the live preset.</summary>
    string ActivePresetId { get; }

    /// <summary>Makes <paramref name="presetId"/> live. An id that names no preset is ignored.</summary>
    void SetActivePreset(string presetId);

    /// <summary>Makes the next preset in tab order live, wrapping to the first.</summary>
    void NextPreset();
}
