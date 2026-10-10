using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtcLens.Infrastructure;

public static class JsonCodec
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Stored JSON is empty.");
}
