using System.Text.Json;

namespace Volco.Config;

public class ConfigManager
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppConfig Config { get; private set; } = new();
    public event EventHandler? ConfigChanged;

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    public void Load()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
        }
        else
        {
            Config = AppConfig.CreateDefault();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(_configPath, json);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}
