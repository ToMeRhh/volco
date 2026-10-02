namespace Volco.Core.Audio;

/// <summary>What kind of device an endpoint is, as Windows reports it (PKEY_AudioEndpoint_FormFactor),
/// folded to what Volco uses to pick an automatic icon (spec §5.2).</summary>
public enum DeviceFormFactor
{
    Unknown,
    Speakers,
    Headphones,
    Headset,
    Microphone,
    Display,
    Other,
}
