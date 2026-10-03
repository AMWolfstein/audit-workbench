using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuditWorkbench.Application.Handover;

/// <summary>AWB-CLIENT canonical JSON: UTF-8, no BOM, sorted object keys, compact, LF.</summary>
internal static class CanonicalJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static byte[] Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options)
                   ?? throw new InvalidOperationException("Cannot serialize a null package value.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteSorted(writer, node);
        }
        return stream.ToArray();
    }

    public static byte[] SerializeJsonLines<T>(IEnumerable<T> values)
    {
        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            var bytes = Serialize(value);
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
        }
        return stream.ToArray();
    }

    public static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new JsonException($"Package entry did not contain a {typeof(T).Name} value.");

    public static List<T> DeserializeJsonLines<T>(byte[] bytes)
    {
        var values = new List<T>();
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            values.Add(JsonSerializer.Deserialize<T>(line, Options)
                       ?? throw new JsonException("A JSONL entry was null."));
        }
        return values;
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer, Options);
                break;
        }
    }
}
