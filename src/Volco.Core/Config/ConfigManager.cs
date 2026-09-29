using System.Text.Json;
using System.Threading;
using Volco.Core.Diagnostics;

namespace Volco.Core.Config;

public class ConfigManager
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _saveLock = new();
    private Timer? _debounceTimer;
    private bool _pendingConnectionChange;
    private const int DebounceMs = 400;

    public AppConfig Config { get; private set; } = new();
    public event EventHandler? ConfigChanged;
    public event EventHandler? ConnectionConfigChanged;

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    public void Load()
    {
        if (File.Exists(_configPath))
        {
            AppLog.Info($"Loading config from {_configPath}");
            try
            {
                var json = File.ReadAllText(_configPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
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
                Config = AppConfig.CreateDefault();
            }
        }
        else
        {
            AppLog.Info("Config file not found, using default config");
            Config = AppConfig.CreateDefault();
        }

        // Defensive: a valid-but-sparse config (hand-edited) must still have entries to index.
        while (Config.Sliders.Count < 5) Config.Sliders.Add(new SliderConfig { Index = Config.Sliders.Count });
        while (Config.Buttons.Count < 3) Config.Buttons.Add(new ButtonConfig { Index = Config.Buttons.Count });

        AppLog.IsDebugEnabled = Config.DebugLogging;
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
        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }
}
