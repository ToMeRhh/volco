using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Volco.Core.Diagnostics;

namespace Volco.Core.Config;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly object _saveLock = new();
    private Timer? _debounceTimer;
    private bool _savePending;   // under _saveLock: a debounced save hasn't been written yet
    private bool _pendingConnectionChange;
    private const int DebounceMs = 400;

    /// <summary>The config.json path this manager loads from and saves to.</summary>
    public string ConfigPath => _configPath;

    public AppConfig Config { get; private set; } = new();
    public event EventHandler? ConfigChanged;
    public event EventHandler? ConnectionConfigChanged;

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    public void Load()
    {
        var migratedFrom = 0;
        if (File.Exists(_configPath))
        {
            AppLog.Info($"Loading config from {_configPath}");
            try
            {
                var json = File.ReadAllText(_configPath);
                if (JsonNode.Parse(json) is JsonObject root)
                {
                    var version = root["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
                    if (version < 3)
                    {
                        // v1 and v2 both get the v3 shape here. v2 is then current; v1 keeps its
                        // number until the runtime resolves its device names (ConfigMigrator.MigrateFile).
                        ConfigMigrator.UpgradeShapeToV3(root);
                        if (version == 2)
                        {
                            BackupBeforeMigration(json, "config.v2.bak");
                            root["schemaVersion"] = AppConfig.CurrentSchemaVersion;
                            migratedFrom = 2;
                        }
                        json = root.ToJsonString();
                    }
                    else if (version is 3 or 4 or 5 or 6 or 7)
                    {
                        // v3 → v4 only adds "devices" and "controller", v4 → v5 only adds
                        // "controllerConfigBlob" (and drops the baud rate, below), and v5 → v6 only
                        // adds the media, program and script action fields, and v7 → v8 moves the button timing
                        // (below); the new fields
                        // deserialize to their defaults when missing: stamp the version, keep a backup.
                        BackupBeforeMigration(json, $"config.v{version}.bak");
                        root["schemaVersion"] = AppConfig.CurrentSchemaVersion;
                        migratedFrom = version;
                    }
                    // v6 → v7 renames presets to modes; every older file passes through it too.
                    if (version < 7) ConfigMigrator.RenamePresetsToModes(root);
                    // v7 → v8: one button timing for the controller; sliders drop invert, curve and dead zone.
                    if (version < 8) ConfigMigrator.MoveButtonTiming(root);
                    // The serial baud rate is fixed at 115200 since v5 (spec §8.1).
                    if (root["connection"]?["serial"] is JsonObject serial) serial.Remove("baudRate");
                    json = root.ToJsonString();
                }
                Config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? AppConfig.CreateDefault();
            }
            catch (Exception ex)
            {
                // A truncated, hand-edited, or otherwise-unreadable config must not stop the app
                // from starting (review item 8) - the catch is deliberately widened from
                // JsonException/IOException/UnauthorizedAccessException to Exception, since a
                // hand-edited file can fail in ways beyond those three (e.g. an
                // InvalidOperationException from DeviceRefConfigJsonConverter.Read calling
                // reader.GetString() on a non-string "id"). The whole point of this handler is
                // that startup must not fail. But falling back to defaults must not silently
                // discard the file: the very next RequestSave() would overwrite it, so back it up
                // first.
                AppLog.Error($"Config at {_configPath} could not be read ({ex.GetType().Name}: {ex.Message}); using defaults");
                BackupCorruptConfig();
                migratedFrom = 0; // never overwrite an unreadable file with the defaults
                Config = AppConfig.CreateDefault();
            }
        }
        else
        {
            AppLog.Info("Config file not found, using default config");
            Config = AppConfig.CreateDefault();
        }

        // The number of sliders and buttons is the user's (position == hardware input), so a short
        // list is not padded; Normalize only repairs invariants (a mode exists, Index == position).
        Config.Normalize();

        // Outside the try above: a failed write must not be mistaken for an unreadable file.
        if (migratedFrom != 0)
        {
            AppLog.Info($"Migrated config from schema v{migratedFrom} to v{AppConfig.CurrentSchemaVersion}");
            try { WriteToDisk(); }
            catch (Exception ex) { AppLog.Error($"Could not write the migrated config: {ex}"); }
        }

        AppLog.IsDebugEnabled = Config.DebugLogging;
    }

    /// <summary>Keeps the pre-migration file as <paramref name="name"/> next to config.json, once
    /// (an existing backup from an interrupted earlier run is never overwritten).</summary>
    private void BackupBeforeMigration(string raw, string name)
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            var backup = string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
            if (!File.Exists(backup)) File.WriteAllText(backup, raw);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not write {name}: {ex}");
        }
    }

    /// <summary>Copies an unreadable config.json aside as "config.corrupt.&lt;timestamp&gt;.bak"
    /// before it gets replaced with defaults (review item 8), so a hand-edit mistake or a
    /// corrupted write is recoverable instead of silently lost the moment anything saves.
    /// Best-effort: a failure here must not itself stop startup, so it is only logged.</summary>
    private void BackupCorruptConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            var backupPath = string.IsNullOrEmpty(dir)
                ? $"{_configPath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak"
                : Path.Combine(dir, $"config.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak");
            File.Copy(_configPath, backupPath, overwrite: true);
            AppLog.Info($"Backed up unreadable config to {backupPath}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not back up unreadable config at {_configPath}: {ex}");
        }
    }

    public void RequestSave() => RequestSave(connectionChanged: false);

    public void RequestSave(bool connectionChanged)
    {
        lock (_saveLock)
        {
            _pendingConnectionChange |= connectionChanged;
            _savePending = true;
            _debounceTimer ??= new Timer(_ => FlushDebounced(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void FlushDebounced()
    {
        bool connChanged;
        lock (_saveLock)
        {
            connChanged = _pendingConnectionChange;
            _pendingConnectionChange = false;
            _savePending = false;
        }

        try
        {
            WriteToDisk();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Debounced config save failed: {ex}");
            return;
        }

        // A connection-config save raises ONLY ConnectionConfigChanged (which App.xaml.cs
        // listens to in order to restart the hardware connection). All other saves raise
        // ConfigChanged. The two events are intentionally mutually exclusive: conflating
        // them would cause unnecessary connection restarts on unrelated config changes.
        if (connChanged) ConnectionConfigChanged?.Invoke(this, EventArgs.Empty);
        else ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes a pending debounced save now (shutdown): the 400 ms timer would never fire.
    /// Raises the event the timer would have. Does nothing when no save is pending; never throws.</summary>
    public void FlushPendingSave()
    {
        try
        {
            lock (_saveLock)
            {
                if (!_savePending) return;
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            FlushDebounced();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Config flush at exit failed: {ex}");
        }
    }

    public Task SaveAsync() => Task.Run(WriteToDisk);

    public void Save()
    {
        WriteToDisk();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WriteToDisk()
    {
        AppLog.Info($"Saving config to {_configPath}");
        var dir = Path.GetDirectoryName(_configPath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        // Position is identity: keep the readable Index fields in step with the list order.
        for (int i = 0; i < Config.Sliders.Count; i++) Config.Sliders[i].Index = i;
        for (int i = 0; i < Config.Buttons.Count; i++) Config.Buttons[i].Index = i;
        var json = JsonSerializer.Serialize(Config, ConfigJsonContext.Default.AppConfig);
        File.WriteAllText(_configPath, json);
    }
}
