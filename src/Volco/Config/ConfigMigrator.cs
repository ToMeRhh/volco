using System.Text.Json;
using Volco.Audio;

namespace Volco.Config;

/// <summary>
/// Migrates config from schema v1 (device references stored as bare friendly-name
/// substrings) to v2 (endpoint ids, with the name kept for display and fallback).
///
/// Reads the raw JSON rather than the deserialized <see cref="AppConfig"/>, because the
/// v1 property names no longer exist on the v2 types and System.Text.Json drops them
/// silently on load.
/// </summary>
public static class ConfigMigrator
{
    public static bool NeedsMigration(AppConfig config)
        => config.SchemaVersion < AppConfig.CurrentSchemaVersion;

    /// <summary>
    /// Resolves a v1 name to a device: exact friendly-name match first, then the legacy
    /// substring match v1 relied on. Null when nothing matches.
    /// </summary>
    public static DeviceRefConfig? ResolveName(string? name, IReadOnlyList<AudioDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return new DeviceRefConfig { Id = exact.Id, FriendlyName = exact.FriendlyName };

        var partial = devices.FirstOrDefault(d =>
            d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (partial != null)
            return new DeviceRefConfig { Id = partial.Id, FriendlyName = partial.FriendlyName };

        return null;
    }

    /// <summary>Unresolvable names keep the name so the UI can prompt for reselection.</summary>
    private static DeviceRefConfig Preserve(string? name, IReadOnlyList<AudioDevice> devices)
        => ResolveName(name, devices) ?? new DeviceRefConfig { Id = "", FriendlyName = name ?? "" };

    /// <summary>
    /// Backs up the v1 file, applies the migration to <paramref name="config"/> in place,
    /// and stamps the new schema version. Caller saves. No-op when already current.
    /// </summary>
    public static void MigrateFile(string configPath, AppConfig config, IReadOnlyList<AudioDevice> devices)
    {
        if (!NeedsMigration(config)) return;

        try
        {
            if (File.Exists(configPath))
            {
                var raw = File.ReadAllText(configPath);

                var dir = Path.GetDirectoryName(configPath);
                if (dir != null)
                {
                    var backup = Path.Combine(dir, "config.v1.bak");
                    if (!File.Exists(backup)) File.WriteAllText(backup, raw);
                }

                Migrate(config, raw, devices);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Config migration could not read {configPath} ({ex.GetType().Name}: {ex.Message}); stamping v2 with defaults");
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
    }

    /// <summary>Applies v1 device names from <paramref name="rawV1Json"/> onto <paramref name="config"/>.</summary>
    internal static void Migrate(AppConfig config, string rawV1Json, IReadOnlyList<AudioDevice> devices)
    {
        using var doc = JsonDocument.Parse(rawV1Json);
        var root = doc.RootElement;

        if (root.TryGetProperty("sliders", out var sliders) && sliders.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var slider in sliders.EnumerateArray())
            {
                if (i >= config.Sliders.Count) break;
                var name = slider.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null;
                if (config.Sliders[i].Type == SliderMappingType.Device && !string.IsNullOrWhiteSpace(name))
                    config.Sliders[i].Device = Preserve(name, devices);
                i++;
            }
        }

        if (root.TryGetProperty("outputDevices", out var od))
        {
            if (od.TryGetProperty("device1", out var d1))
                config.OutputDevices.Device1 = Preserve(d1.GetString(), devices);
            if (od.TryGetProperty("device2", out var d2))
                config.OutputDevices.Device2 = Preserve(d2.GetString(), devices);
        }

        if (root.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var button in buttons.EnumerateArray())
            {
                if (i >= config.Buttons.Count) break;
                MigrateAction(button, "singlePress", config.Buttons[i].SinglePress, devices);
                MigrateAction(button, "doublePress", config.Buttons[i].DoublePress, devices);
                MigrateAction(button, "longPress",   config.Buttons[i].LongPress,   devices);
                i++;
            }
        }
    }

    private static void MigrateAction(JsonElement button, string property,
        ButtonActionConfig target, IReadOnlyList<AudioDevice> devices)
    {
        if (!button.TryGetProperty(property, out var action)) return;
        if (!action.TryGetProperty("deviceId", out var idElement)) return;

        var name = idElement.GetString();
        if (!string.IsNullOrWhiteSpace(name))
            target.Device = Preserve(name, devices);
    }
}
