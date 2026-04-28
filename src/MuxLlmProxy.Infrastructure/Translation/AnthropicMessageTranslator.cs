using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;

namespace MuxLlmProxy.Infrastructure.Translation;

/// <summary>
/// Translates Anthropic Messages payloads using the same block-oriented conversion model
/// used by the reference Claude proxy.
/// </summary>
public sealed class AnthropicMessageTranslator : IMessageTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public byte[] ToOpenAiRequest(byte[] anthropicJson)
    {
        return AnthropicToOpenAiConverter.ConvertRequest(anthropicJson);
    }

    /// <inheritdoc />
    public byte[] ToAnthropicResponse(byte[] openAiJson, string model)
    {
        using var document = JsonDocument.Parse(openAiJson);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var reasoning = message.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String
            ? reasoningElement.GetString() ?? string.Empty
            : string.Empty;

        var contentBlocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            contentBlocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "thinking",
                ["thinking"] = reasoning
            });
        }

        if (!string.IsNullOrEmpty(content))
        {
            contentBlocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = content
            });
        }

        contentBlocks.AddRange(ExtractToolUseBlocks(message));
        if (contentBlocks.Count == 0)
        {
            contentBlocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = " "
            });
        }

        var promptTokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var prompt)
            ? prompt.GetInt32()
            : 0;
        var completionTokens = root.TryGetProperty("usage", out usage) && usage.TryGetProperty("completion_tokens", out var completion)
            ? completion.GetInt32()
            : 0;
        var finishReason = contentBlocks.Any(IsToolUseBlock)
            ? "tool_use"
            : choice.TryGetProperty("finish_reason", out var finishElement)
                ? MapStopReason(finishElement.GetString())
                : "end_turn";

        var payload = new Dictionary<string, object?>
        {
            ["id"] = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = contentBlocks,
            ["stop_reason"] = finishReason,
            ["stop_sequence"] = null,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = promptTokens,
                ["output_tokens"] = completionTokens
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    /// <inheritdoc />
    public byte[] ToAnthropicStream(byte[] openAiStreamBytes, string model)
    {
        return AnthropicStreamConverter.ConvertOpenAiStream(openAiStreamBytes, model);
    }

    private static IEnumerable<Dictionary<string, object?>> ExtractToolUseBlocks(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var argumentsText = functionElement.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.ValueKind == JsonValueKind.String ? argumentsElement.GetString() ?? string.Empty : argumentsElement.GetRawText()
                : string.Empty;

            yield return new Dictionary<string, object?>
            {
                ["type"] = "tool_use",
                ["id"] = AnthropicContent.GetString(toolCall, "id"),
                ["name"] = AnthropicContent.GetString(functionElement, "name"),
                ["input"] = AnthropicContent.ParseJsonOrRaw(argumentsText)
            };
        }
    }

    private static bool IsToolUseBlock(object block)
    {
        return block is Dictionary<string, object?> dictionary
            && dictionary.TryGetValue("type", out var type)
            && type?.ToString() == "tool_use";
    }

    private static string MapStopReason(string? openAiReason)
    {
        return openAiReason switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            _ => "end_turn"
        };
    }
}
