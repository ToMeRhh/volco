using System.Text.Json.Serialization;
using Volco.Core.Audio;
using Volco.Core.Communication;

namespace Volco.Core.Config;

/// <summary>
/// The user's settings for one audio device (Devices page, spec §5.1). Only devices the user
/// customized have an entry; an entry with no nickname and no icon is removed.
/// </summary>
public class DeviceSettingsConfig
{
    /// <summary>The endpoint id: the device's identity.</summary>
    public string Id { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<AudioFlow>))]
    public AudioFlow Flow { get; set; } = AudioFlow.Render;

    /// <summary>The last name Windows reported, so a disconnected device still has a name.</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>"" = use the automatic short name.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>null = automatic (from the device's form factor).</summary>
    public ScreenIcon? Icon { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Nickname) && Icon is null;
}

/// <summary>The last Hello the controller sent (spec §5.1), so the UI knows the hardware while
/// the controller is unplugged.</summary>
public class ControllerInfoConfig
{
    public int ProtocolVersion { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ChipFamily>))]
    public ChipFamily Chip { get; set; }

    /// <summary>The firmware build id the controller reported (spec §9.2).</summary>
    public string FirmwareVersion { get; set; } = "";
    public int Sliders { get; set; }

    /// <summary>Indicator kind per button, by hardware input number.</summary>
    public List<IndicatorKind> Buttons { get; set; } = new();

    public bool SameAs(ControllerInfoConfig? other)
        => other is not null && ProtocolVersion == other.ProtocolVersion && Chip == other.Chip
           && FirmwareVersion == other.FirmwareVersion && Sliders == other.Sliders
           && Buttons.SequenceEqual(other.Buttons);
}
