using System.Text.Json;
using System.Text.Json.Nodes;
using Volco.Core.Audio;
using Volco.Core.Diagnostics;

namespace Volco.Core.Config;

/// <summary>
/// Config schema migrations.
///
/// <list type="bullet">
/// <item>v1 → v2: device references stored as bare friendly-name substrings become endpoint
/// ids (with the name kept for display and fallback). Needs the live device list, so the
/// runtime runs it (<see cref="MigrateFile"/>) once audio is up.</item>
/// <item>v2 → v3: button actions move under a preset (<c>buttons[i].actions["default"]</c>)
/// and the global toggle pair (<c>outputDevices</c>) moves into each ToggleOutputDevice
/// action. Purely structural, so <see cref="ConfigManager.Load"/> runs it
/// (<see cref="UpgradeShapeToV3"/>) on the raw JSON before deserialising - for v1 files too,
/// which then still carry their old schema number until the v1 → v2 name resolution runs.</item>
/// <item>v6 → v7: button presets become modes (<see cref="RenamePresetsToModes"/>), run by
/// <see cref="ConfigManager.Load"/> on every older file after the shape upgrade.</item>
/// </list>
///
/// Both read raw JSON rather than the deserialized <see cref="AppConfig"/>, because the old
/// property names no longer exist on the current types and System.Text.Json drops them
/// silently on load.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>True only for a schema v1 file (no "schemaVersion", or 0/1): its device names still
    /// need resolving against live devices. v2 and v3 are upgraded in <see cref="ConfigManager.Load"/>
    /// and must never come through here (that would write config.v1.bak and re-parse them as v1).</summary>
    public static bool NeedsMigration(AppConfig config)
        => config.SchemaVersion < 2;

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
            AppLog.Error($"Config migration could not read {configPath} ({ex.GetType().Name}: {ex.Message}); stamping v{AppConfig.CurrentSchemaVersion} with defaults");
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

        if (root.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var button in buttons.EnumerateArray())
            {
                if (i >= config.Buttons.Count) break;
                var binding = config.Buttons[i].GetOrAddBinding(AppConfig.DefaultModeId);
                MigrateAction(button, "singlePress", binding.SinglePress, devices);
                MigrateAction(button, "doublePress", binding.DoublePress, devices);
                MigrateAction(button, "longPress",   binding.LongPress,   devices);
                i++;
            }
        }

        // v1 kept the toggle pair globally; v3 keeps it on each toggle action (Device = A,
        // DeviceB = B). UpgradeShapeToV3 already copied the bare names there at load time;
        // resolve them to ids now.
        if (root.TryGetProperty("outputDevices", out var od))
        {
            DeviceRefConfig? a = od.TryGetProperty("device1", out var d1) ? Preserve(d1.GetString(), devices) : null;
            DeviceRefConfig? b = od.TryGetProperty("device2", out var d2) ? Preserve(d2.GetString(), devices) : null;
            foreach (var button in config.Buttons)
                foreach (var binding in button.Actions.Values)
                    foreach (var action in binding.All())
                    {
                        if (action.Action != ButtonActionType.ToggleOutputDevice) continue;
                        if (a != null) action.Device = Clone(a);
                        if (b != null) action.DeviceB = Clone(b);
                    }
        }
    }

    private static DeviceRefConfig Clone(DeviceRefConfig d) => new() { Id = d.Id, FriendlyName = d.FriendlyName };

    /// <summary>
    /// v2 → v3 structural upgrade on the raw config JSON (camelCase keys, as
    /// <see cref="ConfigManager"/> writes them). In place; idempotent. Does not touch
    /// <c>schemaVersion</c> - the caller decides whether this file is now v3 (it was v2) or
    /// still needs the v1 device-name resolution.
    /// <list type="bullet">
    /// <item><c>buttons[i].singlePress/doublePress/longPress</c> move to
    /// <c>buttons[i].actions["default"]</c>.</item>
    /// <item>Each ToggleOutputDevice action gets <c>device</c> = <c>outputDevices.device1</c> and
    /// <c>deviceB</c> = <c>outputDevices.device2</c>; <c>outputDevices</c> is dropped.</item>
    /// <item><c>presets</c> is added with the single Default preset when absent (v3 named modes
    /// presets; <see cref="RenamePresetsToModes"/> renames them afterwards).</item>
    /// <item><c>index</c> of every slider and button is set to its list position.</item>
    /// </list>
    /// </summary>
    public static void UpgradeShapeToV3(JsonObject root)
    {
        var outputDevices = root["outputDevices"] as JsonObject;
        var deviceA = outputDevices?["device1"];
        var deviceB = outputDevices?["device2"];

        if (root["buttons"] is JsonArray buttons)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] is not JsonObject button) continue;
                button["index"] = i;
                if (button.ContainsKey("actions")) continue;

                var binding = new JsonObject();
                foreach (var key in new[] { "singlePress", "doublePress", "longPress" })
                {
                    if (button[key] is JsonObject action)
                    {
                        button.Remove(key);
                        if (IsAction(action, "ToggleOutputDevice"))
                        {
                            // v2 wrote "device": null on toggle actions; null and absent both mean "unset".
                            if (action["device"] is null && deviceA != null) action["device"] = deviceA.DeepClone();
                            if (action["deviceB"] is null && deviceB != null) action["deviceB"] = deviceB.DeepClone();
                        }
                        binding[key] = action;
                    }
                    else
                    {
                        button.Remove(key);
                    }
                }
                button["actions"] = new JsonObject { [AppConfig.DefaultModeId] = binding };
            }
        }

        if (root["sliders"] is JsonArray sliders)
            for (int i = 0; i < sliders.Count; i++)
                if (sliders[i] is JsonObject slider) slider["index"] = i;

        root.Remove("outputDevices");

        if (root["presets"] is not JsonArray presets || presets.Count == 0)
            root["presets"] = new JsonArray(new JsonObject { ["id"] = AppConfig.DefaultModeId, ["name"] = "Default" });
    }

    /// <summary>
    /// v6 → v7 on the raw config JSON: button presets become modes. In place; idempotent. Run
    /// after <see cref="UpgradeShapeToV3"/>, so v1 to v6 files all come through here.
    /// <list type="bullet">
    /// <item><c>presets</c> becomes <c>modes</c>.</item>
    /// <item>Every action's <c>presetId</c> becomes <c>modeId</c>.</item>
    /// <item>The actions <c>NextPreset</c> and <c>GoToPreset</c> become <c>NextMode</c> and
    /// <c>GoToMode</c>.</item>
    /// </list>
    /// Mode ids (such as "default" and "preset-2") are kept.
    /// </summary>
    public static void RenamePresetsToModes(JsonObject root)
    {
        Rename(root, "presets", "modes");

        if (root["buttons"] is not JsonArray buttons) return;
        foreach (var button in buttons)
        {
            if (button?["actions"] is not JsonObject actions) continue;
            foreach (var (_, binding) in actions)
            {
                if (binding is not JsonObject slots) continue;
                foreach (var key in new[] { "singlePress", "doublePress", "longPress" })
                {
                    if (slots[key] is not JsonObject action) continue;
                    Rename(action, "presetId", "modeId");
                    if (IsAction(action, "NextPreset")) action["action"] = "NextMode";
                    else if (IsAction(action, "GoToPreset")) action["action"] = "GoToMode";
                }
            }
        }
    }

    /// <summary>Moves <paramref name="from"/>'s value to <paramref name="to"/>, unless
    /// <paramref name="to"/> is already there (then <paramref name="from"/> is only dropped).</summary>
    private static void Rename(JsonObject obj, string from, string to)
    {
        if (!obj.TryGetPropertyValue(from, out var value)) return;
        obj.Remove(from);
        if (!obj.ContainsKey(to)) obj[to] = value;
    }

    private static bool IsAction(JsonObject action, string name)
        => action["action"] is JsonValue v && v.TryGetValue<string>(out var s)
           && string.Equals(s, name, StringComparison.OrdinalIgnoreCase);

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

        foreach (var slider in config.Sliders)
            changed |= Heal(slider.Device, devices);

        foreach (var button in config.Buttons)
            foreach (var binding in button.Actions.Values)
            {
                if (binding == null) continue;
                foreach (var action in binding.All())
                {
                    changed |= Heal(action.Device, devices);
                    changed |= Heal(action.DeviceB, devices);
                }
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
