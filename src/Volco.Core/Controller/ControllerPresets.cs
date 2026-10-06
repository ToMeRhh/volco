using Google.Protobuf;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A preset in the hardware picker (spec §6.2). <see cref="Hardware"/> holds only the
/// hardware fields and is a fresh copy on every read, so no caller can change the preset for
/// everyone; it is null for a placeholder that isn't available yet.</summary>
public sealed class ControllerPreset(string id, string displayName, bool isAvailable, ControllerConfig? hardware)
{
    private readonly ControllerConfig? _hardware = hardware;
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool IsAvailable { get; } = isAvailable;
    public ControllerConfig? Hardware => _hardware?.Clone();
}

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
    /// buttons, i2c0..i2c3, preset) replaced by the preset's; behaviour, input tuning and UDP stay.</summary>
    public static ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset)
    {
        var hw = preset.Hardware;
        if (!preset.IsAvailable || hw is null)
            throw new InvalidOperationException($"Preset '{preset.Id}' isn't available.");
        var result = draft.Clone();
        result.Sliders.Clear();
        result.Sliders.AddRange(hw.Sliders.Select(s => s.Clone()));
        result.Buttons.Clear();
        result.Buttons.AddRange(hw.Buttons.Select(b => b.Clone()));
        result.I2C0 = hw.I2C0?.Clone();
        result.I2C1 = hw.I2C1?.Clone();
        result.I2C2 = hw.I2C2?.Clone();
        result.I2C3 = hw.I2C3?.Clone();
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
