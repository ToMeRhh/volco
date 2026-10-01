using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;

namespace Volco.Core.Communication;

/// <summary>
/// Computes what each button's indicator on the controller should show (spec §3.6). Pure and
/// side-effect free: only the read-only engine queries <see cref="IAudioEngine"/> documents as
/// safe from any thread.
/// </summary>
/// <remarks>
/// A button's value comes from the first stateful action of its binding in the live preset, in
/// the order single → double → long press:
/// <list type="bullet">
/// <item>Mute actions: 1 while muted, else 0.</item>
/// <item>SetOutputDevice: 1 while that device is the default output.</item>
/// <item>ToggleOutputDevice: 1 while A is the default output, 2 while B is, else 0.</item>
/// <item>NextPreset / GoToPreset: 1 + the live preset's position.</item>
/// </list>
/// Buttons with no stateful action get 0, so an indicator lit under the previous preset goes off.
/// </remarks>
public static class ButtonFeedback
{
    /// <summary>One value per button, indexed by hardware input.</summary>
    public static IReadOnlyList<byte> Compute(AppConfig config, string activePresetId, IAudioEngine audio)
    {
        var values = new byte[config.Buttons.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = ValueFor(config.Buttons[i].BindingFor(activePresetId), config, activePresetId, audio) ?? 0;
        return values;
    }

    /// <summary>The frames for <see cref="Compute"/>'s values.</summary>
    public static IReadOnlyList<byte[]> BuildFrames(AppConfig config, string activePresetId, IAudioEngine audio)
    {
        var values = Compute(config, activePresetId, audio);
        var frames = new byte[values.Count][];
        for (int i = 0; i < values.Count; i++)
            frames[i] = ProtocolEncoder.EncodeButtonFeedback(i, values[i]);
        return frames;
    }

    /// <summary>The value of the first stateful action in <paramref name="binding"/>, or null when
    /// none of its actions has a state.</summary>
    public static byte? ValueFor(ButtonBindingConfig binding, AppConfig config, string activePresetId, IAudioEngine audio)
    {
        foreach (var action in binding.All())
        {
            var value = ValueFor(action, config, activePresetId, audio);
            if (value.HasValue) return value;
        }
        return null;
    }

    /// <summary>The value one action reports, or null when it has no state (None).</summary>
    public static byte? ValueFor(ButtonActionConfig action, AppConfig config, string activePresetId, IAudioEngine audio)
    {
        switch (action.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                return Muted(audio, DeviceRef.DefaultOutput());
            case ButtonActionType.MuteCurrentInput:
                return Muted(audio, DeviceRef.DefaultInput());
            case ButtonActionType.MuteSpecificDevice:
                return ActionFactory.Resolve(action.Device) is { } r ? Muted(audio, r) : (byte)0;
            case ButtonActionType.SetOutputDevice:
            {
                var current = audio.GetDefaultDevice(AudioFlow.Render);
                return current != null && ToggleOutputDeviceAction.DeviceMatches(current, action.Device) ? (byte)1 : (byte)0;
            }
            case ButtonActionType.ToggleOutputDevice:
            {
                var current = audio.GetDefaultDevice(AudioFlow.Render);
                if (current == null) return 0;
                if (ToggleOutputDeviceAction.DeviceMatches(current, action.Device)) return 1;
                if (ToggleOutputDeviceAction.DeviceMatches(current, action.DeviceB)) return 2;
                return 0;
            }
            case ButtonActionType.NextPreset:
            case ButtonActionType.GoToPreset:
            {
                var index = config.Presets.FindIndex(p => p.Id == activePresetId);
                return index < 0 ? (byte)0 : (byte)Math.Min(255, index + 1);
            }
            default:
                return null;
        }
    }

    private static byte Muted(IAudioEngine audio, DeviceRef device)
        => audio.TryGetDeviceState(device, out _, out var muted) && muted ? (byte)1 : (byte)0;
}
