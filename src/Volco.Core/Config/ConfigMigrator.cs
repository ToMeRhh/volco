using System.Text.Json;
using Volco.Core.Audio;
using Volco.Core.Diagnostics;

namespace Volco.Core.Config;

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

        if (devices.Count == 0)
        {
            // Nothing to resolve v1 names/ids against yet (e.g. the audio engine failed to start
            // in time - App.xaml.cs already guards against calling this in that case, but this is
            // the load-bearing check). Stamping v2 anyway would turn every reference into
            // {Id:"", FriendlyName:<name>} and make NeedsMigration false forever, permanently
            // disabling anything keyed on a resolved id (e.g. output-device switching - review
            // item 5). Leave the config untouched so a later launch, with a real device list,
            // retries.
            AppLog.Info("Config migration skipped: no audio devices available yet; will retry on a later launch.");
            return;
        }

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

    /// <summary>
    /// Fix C: fills in the endpoint id for any <see cref="DeviceRefConfig"/> that has an empty
    /// <see cref="DeviceRefConfig.Id"/> but a non-empty <see cref="DeviceRefConfig.FriendlyName"/>,
    /// when a device's friendly name matches it EXACTLY (OrdinalIgnoreCase). This is what repairs
    /// a reference <see cref="Preserve"/> left blank because the target device was absent at
    /// migration time (or a v2 config with an id manually removed) — <see cref="ConfigMigrator"/>
    /// itself only runs once, for schema v1, so nothing else ever revisits these.
    ///
    /// Deliberately does NOT fall back to the v1 substring match <see cref="ResolveName"/> uses:
    /// this can run at any later point in the app's life, against whatever devices happen to be
    /// plugged in right now, so a loose match binding output switching to the wrong endpoint would
    /// be a worse outcome than leaving the reference unhealed for another day. Never touches a
    /// reference that already has a non-empty id. For the same reason, a friendly name matched by
    /// MORE than one present device is left unhealed too — an ambiguous name is exactly the case
    /// where guessing which endpoint was meant risks binding to the wrong one.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool HealDeviceIds(AppConfig config, IReadOnlyList<AudioDevice> devices)
    {
        var changed = false;

        changed |= Heal(config.OutputDevices.Device1, devices);
        changed |= Heal(config.OutputDevices.Device2, devices);

        foreach (var slider in config.Sliders)
            changed |= Heal(slider.Device, devices);

        foreach (var button in config.Buttons)
        {
            changed |= Heal(button.SinglePress.Device, devices);
            changed |= Heal(button.DoublePress.Device, devices);
            changed |= Heal(button.LongPress.Device, devices);
        }

        return changed;
    }

    private static bool Heal(DeviceRefConfig? cfg, IReadOnlyList<AudioDevice> devices)
    {
        if (cfg == null) return false;
        if (!string.IsNullOrEmpty(cfg.Id)) return false;
        if (string.IsNullOrEmpty(cfg.FriendlyName)) return false;

        var matches = devices.Where(d =>
            string.Equals(d.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0) return false;

        if (matches.Count > 1)
        {
            // Ambiguous: more than one present device shares this friendly name. Heal's own
            // principle (see the class doc above) is that a wrong binding is worse than no
            // binding, so this reference is left blank rather than guessing.
            AppLog.Info($"Device id heal: \"{cfg.FriendlyName}\" matches {matches.Count} present " +
                        "devices; leaving this reference unresolved rather than guessing.");
            return false;
        }

        var match = matches[0];
        cfg.Id = match.Id;
        cfg.FriendlyName = match.FriendlyName; // normalise to the device's own casing
        return true;
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
