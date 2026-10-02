using System.Text.RegularExpressions;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;

namespace Volco.Core.Devices;

/// <summary>How a device appears everywhere Volco names it: screens, the OSD, the settings UI.</summary>
public readonly record struct DeviceLook(string Name, ScreenIcon Icon);

/// <summary>
/// Display names and icons for devices (spec §2.2, §5.3). Name: the nickname, else the automatic
/// short name (the Windows name minus a trailing parenthesised part). Icon: the chosen icon, else
/// the one for the device's form factor, else <see cref="ScreenIcon.Generic"/>.
/// </summary>
public static partial class DeviceDisplay
{
    [GeneratedRegex(@"^(.*?)\s*\(.*\)\s*$")]
    private static partial Regex TrailingParenthesis();

    /// <summary>"Speakers (Realtek High Definition Audio)" → "Speakers". A name that would be
    /// empty without its parentheses is kept whole.</summary>
    public static string AutoShortName(string? friendlyName)
    {
        var name = (friendlyName ?? "").Trim();
        var m = TrailingParenthesis().Match(name);
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : name;
    }

    public static ScreenIcon IconFor(DeviceFormFactor formFactor) => formFactor switch
    {
        DeviceFormFactor.Speakers => ScreenIcon.Speaker,
        DeviceFormFactor.Headphones => ScreenIcon.Headphones,
        DeviceFormFactor.Headset => ScreenIcon.Headset,
        DeviceFormFactor.Microphone => ScreenIcon.Mic,
        DeviceFormFactor.Display => ScreenIcon.Display,
        _ => ScreenIcon.Generic,
    };

    /// <summary>A device Windows reports right now.</summary>
    public static DeviceLook For(AppConfig config, AudioDevice device)
    {
        var settings = config.FindDevice(device.Id);
        return new DeviceLook(NameFrom(settings, device.FriendlyName), settings?.Icon ?? IconFor(device.FormFactor));
    }

    /// <summary>A configured reference, which may be disconnected. Null when the reference names
    /// nothing (no id and no name).</summary>
    public static DeviceLook? For(AppConfig config, IAudioEngine audio, DeviceRefConfig? reference)
    {
        if (reference is null || (string.IsNullOrEmpty(reference.Id) && string.IsNullOrEmpty(reference.FriendlyName)))
            return null;
        if (FindLive(audio, reference) is { } live) return For(config, live);
        var settings = config.FindDevice(reference.Id);
        var friendly = settings?.FriendlyName is { Length: > 0 } stored ? stored : reference.FriendlyName;
        return new DeviceLook(NameFrom(settings, friendly), settings?.Icon ?? ScreenIcon.Generic);
    }

    /// <summary>The display name of a reference, or <paramref name="fallback"/> when it names nothing.</summary>
    public static string NameOf(AppConfig config, IAudioEngine audio, DeviceRefConfig? reference, string fallback)
        => For(config, audio, reference)?.Name ?? fallback;

    /// <summary>The live device a reference points at (by id, else by friendly name), either flow.</summary>
    public static AudioDevice? FindLive(IAudioEngine audio, DeviceRefConfig? reference)
    {
        if (reference is null) return null;
        var all = audio.GetDevices(AudioFlow.Render).Concat(audio.GetDevices(AudioFlow.Capture)).ToList();
        if (!string.IsNullOrEmpty(reference.Id))
            return all.Find(d => string.Equals(d.Id, reference.Id, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(reference.FriendlyName))
            return all.Find(d => string.Equals(d.FriendlyName, reference.FriendlyName, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static string NameFrom(DeviceSettingsConfig? settings, string friendlyName)
        => !string.IsNullOrWhiteSpace(settings?.Nickname) ? settings!.Nickname.Trim() : AutoShortName(friendlyName);
}
