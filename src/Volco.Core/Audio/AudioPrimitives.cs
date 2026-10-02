namespace Volco.Core.Audio;

public enum AudioFlow { Render, Capture }

public enum DeviceRefKind { Default, ById, ByFriendlyName }

/// <summary>
/// Identifies a device without binding to a COM object: the default endpoint for a
/// flow, a specific endpoint id, or a friendly name (the fallback used while
/// config carries no id yet, and by the transitional legacy adapter).
/// </summary>
public readonly record struct DeviceRef(DeviceRefKind Kind, AudioFlow Flow, string Id)
{
    public static DeviceRef DefaultOutput()
        => new(DeviceRefKind.Default, AudioFlow.Render, "");

    public static DeviceRef DefaultInput()
        => new(DeviceRefKind.Default, AudioFlow.Capture, "");

    public static DeviceRef ById(string endpointId, AudioFlow flow = AudioFlow.Render)
        => new(DeviceRefKind.ById, flow, endpointId);

    public static DeviceRef ByFriendlyName(string friendlyName, AudioFlow flow = AudioFlow.Render)
        => new(DeviceRefKind.ByFriendlyName, flow, friendlyName);
}

public sealed record AudioDevice(
    string    Id,
    string    FriendlyName,
    AudioFlow Flow,
    bool      IsDefault,
    DeviceFormFactor FormFactor = DeviceFormFactor.Unknown);

public sealed record AudioSessionInfo(
    string InstanceId,
    uint   ProcessId,
    string ImageName,
    string DeviceId,
    bool   IsSystemSounds);

/// <summary>Selects which live sessions a volume write applies to.</summary>
public abstract record SessionFilter
{
    private SessionFilter() { }

    public sealed record ByImageNames(IReadOnlyCollection<string> Names) : SessionFilter;

    /// <summary>Every live session whose image name is NOT in <paramref name="MappedNames"/>.</summary>
    public sealed record Unmapped(IReadOnlyCollection<string> MappedNames) : SessionFilter;
}
