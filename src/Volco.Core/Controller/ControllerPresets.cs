using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A preset in the hardware picker (spec §6.2). <see cref="Hardware"/> holds only the
/// hardware fields; it is null for a placeholder that isn't available yet.</summary>
public sealed record ControllerPreset(string Id, string DisplayName, bool IsAvailable, ControllerConfig? Hardware);

public static class ControllerPresets
{
    public const string DiyReferenceId = "diy-reference";
    public const string VolcoPcbId = "volco-pcb";
    public const string BlankId = "blank";

    public static IReadOnlyList<ControllerPreset> All => throw new NotImplementedException();
    public static ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset) => throw new NotImplementedException();
}
