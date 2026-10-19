namespace Volco.Core.Config;

/// <summary>The rule for a slider's target, shared by the UI (to mark the field) and the runtime.</summary>
public static class SliderTargetValidator
{
    /// <summary>The problem with the slider's target, or null when it is valid. A null device is
    /// the main output, so it is valid.</summary>
    public static string? Validate(SliderConfig slider)
        => slider.Type == SliderMappingType.Processes && slider.Processes.Count == 0
            ? "Tick at least one app, or choose a device."
            : null;
}
