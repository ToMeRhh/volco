using Volco.Core.Audio;

namespace Volco.Windows.Audio;

/// <summary>
/// Core Audio's three-role default-device model (spec §4.4). This is a Windows-only concept -
/// it does not appear anywhere in <c>Volco.Core.Audio</c>'s public surface - kept here purely so
/// <see cref="DeviceRegistry"/> can track the three defaults Windows itself maintains
/// independently (<c>SetDefaultDevice</c> must still write all three; the OS can also report them
/// changing independently of one another). Everything above the engine sees only
/// <c>AudioFlow.Render</c>/<c>AudioFlow.Capture</c>, mapped onto one of these roles by
/// <see cref="AudioRoleMapping"/>.
/// </summary>
internal enum AudioRole { Console, Multimedia, Communications }

/// <summary>
/// The sole mapping from the public, platform-neutral <see cref="AudioFlow"/> to Windows'
/// <see cref="AudioRole"/> (spec §4.4): Render → Multimedia, Capture → Communications.
/// <see cref="AudioRole.Console"/> has no caller outside this mapping's own bookkeeping in
/// <see cref="DeviceRegistry"/>. Referenced from <see cref="AudioEngine.GetDefaultDevice"/>,
/// <see cref="DeviceRegistry.Resolve"/> (for a <c>DeviceRefKind.Default</c> reference), and
/// <see cref="DeviceRegistry.ToAudioDeviceLocked"/> (for <c>AudioDevice.IsDefault</c>) - the same
/// rule everywhere, so "default" means the same device in all three places.
/// </summary>
internal static class AudioRoleMapping
{
    public static AudioRole ForFlow(AudioFlow flow)
        => flow == AudioFlow.Render ? AudioRole.Multimedia : AudioRole.Communications;
}
