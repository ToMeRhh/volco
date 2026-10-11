using System.Text.Json.Serialization;

namespace Volco.Core.Config;

/// <summary>
/// The source-generated JSON contract for config.json. The published Volco.exe is trimmed, and a
/// trimmed app has reflection-based System.Text.Json turned off, so every (de)serialisation of the
/// config goes through this context.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ButtonConfig))]
[JsonSerializable(typeof(ButtonBindingConfig))]
public partial class ConfigJsonContext : JsonSerializerContext
{
}
