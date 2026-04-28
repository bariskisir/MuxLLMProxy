using System.Text.Json;

namespace MuxLlmProxy.Infrastructure.Translation;

internal static class AnthropicContent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string? GetBlockType(JsonElement block)
    {
        return block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
    }

    public static string GetString(JsonElement block, string propertyName, string fallback = "")
    {
        return block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback
                : fallback;
    }

    public static string SerializeToolResultContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Array => SerializeToolResultArray(content),
            _ => content.GetRawText()
        };
    }

    public static object JsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null!,
            _ => JsonSerializer.Deserialize<object>(element.GetRawText(), SerializerOptions)!
        };
    }

    public static object ParseJsonOrRaw(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<object>(value, SerializerOptions) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>
            {
                ["raw_arguments"] = value
            };
        }
    }

    private static string SerializeToolResultArray(JsonElement content)
    {
        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && GetBlockType(item) == "text")
            {
                parts.Add(GetString(item, "text"));
            }
            else
            {
                parts.Add(item.GetRawText());
            }
        }

        return string.Join("\n", parts);
    }
}
