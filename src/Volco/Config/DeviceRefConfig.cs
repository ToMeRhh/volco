namespace Volco.Config;

/// <summary>
/// A persisted reference to an audio endpoint. <see cref="Id"/> is authoritative;
/// <see cref="FriendlyName"/> is for display and as a fallback when the id does not
/// resolve (a config copied between machines, or migrated from schema v1).
/// </summary>
public class DeviceRefConfig
{
    public string Id { get; set; } = "";
    public string FriendlyName { get; set; } = "";
}
