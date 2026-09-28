using System.Text.Json;
using System.Text.Json.Serialization;

namespace Volco.Config;

/// <summary>
/// A persisted reference to an audio endpoint. <see cref="Id"/> is authoritative;
/// <see cref="FriendlyName"/> is for display and as a fallback when the id does not
/// resolve (a config copied between machines, or migrated from schema v1).
/// </summary>
[JsonConverter(typeof(DeviceRefConfigJsonConverter))]
public class DeviceRefConfig
{
    public string Id { get; set; } = "";
    public string FriendlyName { get; set; } = "";
}

/// <summary>
/// <see cref="OutputDevicesConfig.Device1"/>/<see cref="OutputDevicesConfig.Device2"/> kept
/// their schema v1 JSON keys ("device1"/"device2") when their type changed from a bare
/// string to <see cref="DeviceRefConfig"/>. Without this converter, every config.json written
/// before schema v2 throws <see cref="JsonException"/> on load. This converter reads both
/// shapes losslessly (so a v1 file loads intact, ready for <c>ConfigMigrator</c> to patch in
/// device ids) and always writes the v2 object shape.
/// </summary>
public class DeviceRefConfigJsonConverter : JsonConverter<DeviceRefConfig?>
{
    public override DeviceRefConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                // Schema v1: a bare friendly-name string.
                return new DeviceRefConfig { Id = "", FriendlyName = reader.GetString() ?? "" };

            case JsonTokenType.StartObject:
                {
                    string id = "";
                    string friendlyName = "";
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            continue;

                        var propertyName = reader.GetString();
                        reader.Read();
                        switch (propertyName)
                        {
                            case "id" or "Id":
                                id = reader.GetString() ?? "";
                                break;
                            case "friendlyName" or "FriendlyName":
                                friendlyName = reader.GetString() ?? "";
                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }
                    return new DeviceRefConfig { Id = id, FriendlyName = friendlyName };
                }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when reading DeviceRefConfig.");
        }
    }

    public override void Write(Utf8JsonWriter writer, DeviceRefConfig? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("friendlyName", value.FriendlyName);
        writer.WriteEndObject();
    }
}
