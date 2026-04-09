using System.Text.Json;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Translation;

internal sealed record NormalizedOpenAiRequest(
    OpenAiChatRequest Source,
    IReadOnlyList<OpenAiMessage> ConversationMessages);

internal static class OpenAiRequestNormalizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static NormalizedOpenAiRequest Normalize(OpenAiChatRequest request, ProxyRequest proxyRequest)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proxyRequest);
        var normalizedMessages = request.Messages
            .Select(message => NormalizeConversationMessage(message))
            .ToArray();

        return new NormalizedOpenAiRequest(request, normalizedMessages);
    }

    public static IReadOnlyList<OpenAiTool>? NormalizeTools(IReadOnlyList<OpenAiTool>? tools, ProxyRequest proxyRequest, IReadOnlyList<OpenAiMessage> conversationMessages)
    {
        ArgumentNullException.ThrowIfNull(proxyRequest);
        ArgumentNullException.ThrowIfNull(conversationMessages);
        return tools;
    }

    public static string FlattenMessageContent(object? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is string text)
        {
            return text;
        }

        if (content is JsonElement element)
        {
            return FlattenJsonElement(element);
        }

        return JsonSerializer.Serialize(content, SerializerOptions);
    }

    private static OpenAiMessage NormalizeConversationMessage(OpenAiMessage message)
    {
        if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var flattened = FlattenMessageContent(message.Content);
        if (string.IsNullOrWhiteSpace(flattened))
        {
            return message with { Content = string.Empty };
        }

        return message with { Content = flattened };
    }

    private static string FlattenJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }
            }

            return string.Join("\n", parts);
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("content", out var nestedContent))
        {
            return FlattenJsonElement(nestedContent);
        }

        return element.ToString();
    }
}
