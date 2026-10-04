using Google.Protobuf;
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

    /// <summary>DIY reference, the Volco PCB placeholder (not available until its pins are known),
    /// then Blank.</summary>
    public static IReadOnlyList<ControllerPreset> All { get; } = new[]
    {
        new ControllerPreset(DiyReferenceId, "DIY reference", true, LoadEmbedded(DiyReferenceId)),
        new ControllerPreset(VolcoPcbId, "Volco PCB (coming soon)", false, null),
        new ControllerPreset(BlankId, "Blank", true, new ControllerConfig { Preset = BlankId }),
    };

    /// <summary>A copy of <paramref name="draft"/> with exactly the hardware fields (sliders,
    /// buttons, i2c0, i2c1, preset) replaced by the preset's; behaviour, input tuning and UDP stay.</summary>
    public static ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset)
    {
        if (!preset.IsAvailable || preset.Hardware is null)
            throw new InvalidOperationException($"Preset '{preset.Id}' isn't available.");
        var result = draft.Clone();
        var hw = preset.Hardware;
        result.Sliders.Clear();
        result.Sliders.AddRange(hw.Sliders.Select(s => s.Clone()));
        result.Buttons.Clear();
        result.Buttons.AddRange(hw.Buttons.Select(b => b.Clone()));
        result.I2C0 = hw.I2C0?.Clone();
        result.I2C1 = hw.I2C1?.Clone();
        result.Preset = hw.Preset;
        return result;
    }

    private static ControllerConfig LoadEmbedded(string id)
    {
        var name = $"Volco.Core.Presets.{id}.json";
        using var stream = typeof(ControllerPresets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded preset {name} not found.");
        using var reader = new StreamReader(stream);
        return JsonParser.Default.Parse<ControllerConfig>(reader.ReadToEnd());
    }
}
