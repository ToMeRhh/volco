using System.Text.Json;
using System.Threading;

namespace Volco.Config;

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
            var json = File.ReadAllText(_configPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
        }
        else
        {
            AppLog.Info("Config file not found, using default config");
            Config = AppConfig.CreateDefault();
        }
        AppLog.IsDebugEnabled = Config.DebugLogging;
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
