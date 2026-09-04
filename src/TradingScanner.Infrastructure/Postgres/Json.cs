using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingScanner.Infrastructure.Postgres;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"Stored {typeof(T).Name} document is empty.");
}
