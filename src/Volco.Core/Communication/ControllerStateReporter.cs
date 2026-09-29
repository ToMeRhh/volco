using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Communication;

/// <summary>
/// Computes the state frames pushed to the controller (mute LEDs, active-device LED).
/// Pure and side-effect free so it can be tested without a transport or real audio.
/// </summary>
public static class ControllerStateReporter
{
    /// <summary>Mute LEDs exist for buttons 0 and 1 only.</summary>
    private const int MuteButtonCount = 2;

    public static IReadOnlyList<byte[]> BuildFrames(AppConfig config, IAudioEngine audio)
    {
        var frames = new List<byte[]>();

        for (int i = 0; i < Math.Min(MuteButtonCount, config.Buttons.Count); i++)
        {
            var muted = GetMuteStateForButton(config.Buttons[i], audio);
            if (muted.HasValue)
                frames.Add(ProtocolEncoder.EncodeMuteState(i, muted.Value));
        }

        var current = audio.GetDefaultDevice(AudioFlow.Render);
        var index = ResolveActiveDeviceIndex(current, config.OutputDevices);
        if (index.HasValue)
            frames.Add(ProtocolEncoder.EncodeActiveDevice(index.Value));

        return frames;
    }

    /// <summary>
    /// 0 or 1 when the current default output is one of the configured toggle devices;
    /// null when it is neither, so the caller sends nothing rather than lighting the
    /// wrong LED.
    /// </summary>
    public static int? ResolveActiveDeviceIndex(AudioDevice? current, OutputDevicesConfig devices)
    {
        if (current == null) return null;
        if (Matches(current, devices.Device1)) return 0;
        if (Matches(current, devices.Device2)) return 1;
        return null;
    }

    private static bool Matches(AudioDevice device, DeviceRefConfig? cfg)
    {
        if (cfg == null) return false;
        if (!string.IsNullOrEmpty(cfg.Id))
            return string.Equals(device.Id, cfg.Id, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(cfg.FriendlyName)
            && string.Equals(device.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mute state of the first mute-type action on the button, or null if it has none.</summary>
    public static bool? GetMuteStateForButton(ButtonConfig button, IAudioEngine audio)
    {
        foreach (var action in new[] { button.SinglePress, button.DoublePress, button.LongPress })
        {
            switch (action.Action)
            {
                case ButtonActionType.MuteCurrentOutput:
                    return Muted(audio, DeviceRef.DefaultOutput());

                case ButtonActionType.MuteCurrentInput:
                    return Muted(audio, DeviceRef.DefaultInput());

                case ButtonActionType.MuteSpecificDevice:
                    var reference = Input.Actions.ActionFactory.Resolve(action.Device);
                    if (reference is not { } r) return null;
                    return Muted(audio, r);
            }
        }
        return null;
    }

    private static bool Muted(IAudioEngine audio, DeviceRef device)
        => audio.TryGetDeviceState(device, out _, out var muted) && muted;
}
