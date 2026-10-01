using Volco.Core.Audio;
using Volco.Core.Config;

namespace Volco.Core.Input.Actions;

public class SetOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRefConfig? _device;

    public SetOutputDeviceAction(IAudioEngine audio, DeviceRefConfig? device)
    {
        _audio = audio;
        _device = device;
    }

    public void Execute()
    {
        var id = ResolveTargetId(_device);
        if (id is null) return;    // unconfigured -> no-op
        _audio.SetDefaultDevice(id, AudioFlow.Render);
    }

    /// <summary>The endpoint id this action switches to, or null when unconfigured. Shared with
    /// <see cref="Runtime.VolcoRuntime"/>, which needs the target before Execute() for the OSD.</summary>
    internal static string? ResolveTargetId(DeviceRefConfig? device)
        => device?.Id is { Length: > 0 } id ? id : null;
}
