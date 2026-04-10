using System.Text.Json;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Converts ChatGPT backend-api response payloads into OpenAI chat completion format.
/// </summary>
internal static class ChatGptResponseConverter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts a completed response payload into an OpenAI chat completion payload.
    /// </summary>
    /// <param name="body">The buffered response body.</param>
    /// <param name="requestModel">The requested model identifier.</param>
    /// <returns>The serialized chat completion payload.</returns>
    public static byte[] ConvertResponseToChatCompletion(byte[] body, string requestModel)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var response = root.TryGetProperty("response", out var responseElement) ? responseElement : root;
        var responseId = response.TryGetProperty("id", out var idElement) ? idElement.GetString() : $"chatcmpl-{Guid.NewGuid():N}";
        var model = response.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : requestModel;
        var created = response.TryGetProperty("created_at", out var createdElement) && createdElement.ValueKind == JsonValueKind.Number
            ? createdElement.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var content = ExtractOutputText(response);
        var toolCalls = ExtractToolCalls(response);
        var reasoningContent = ExtractReasoningText(response);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = responseId,
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = BuildAssistantMessage(content, toolCalls, reasoningContent),
                    ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop"
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    /// <summary>
    /// Extracts the completed response payload from a ChatGPT streaming body.
    /// </summary>
    /// <param name="streamBody">The buffered stream body.</param>
    /// <returns>The completed response JSON bytes.</returns>
    public static byte[] ExtractCompletedResponse(byte[] streamBody)
    {
        var lines = System.Text.Encoding.UTF8.GetString(streamBody).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "response.completed", StringComparison.Ordinal)
                || !root.TryGetProperty("response", out var responseElement))
            {
                continue;
            }

            return System.Text.Encoding.UTF8.GetBytes(responseElement.GetRawText());
        }

        throw new InvalidOperationException("The ChatGPT streaming response did not include a completed response payload.");
    }

    /// <summary>
    /// Builds an OpenAI-compatible assistant message dictionary.
    /// </summary>
    /// <param name="content">The message content.</param>
    /// <param name="toolCalls">The extracted tool calls.</param>
    /// <param name="reasoningContent">The extracted reasoning content.</param>
    /// <returns>A dictionary representing the assistant message.</returns>
    internal static Dictionary<string, object?> BuildAssistantMessage(string content, IReadOnlyList<Dictionary<string, object?>> toolCalls, string reasoningContent)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant"
        };

        if (!string.IsNullOrEmpty(content))
        {
            message["content"] = content;
        }

        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            message["reasoning_content"] = reasoningContent;
        }

        return message;
    }

    /// <summary>
    /// Extracts tool call definitions from a response output array.
    /// </summary>
    /// <param name="response">The response JSON element.</param>
    /// <returns>A list of OpenAI-compatible tool call dictionaries.</returns>
    internal static List<Dictionary<string, object?>> ExtractToolCalls(JsonElement response)
    {
        var toolCalls = new List<Dictionary<string, object?>>();
        if (!response.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return toolCalls;
        }

        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) || !string.Equals(typeElement.GetString(), "function_call", StringComparison.Ordinal))
            {
                continue;
            }

            toolCalls.Add(new Dictionary<string, object?>
            {
                ["id"] = item.TryGetProperty("call_id", out var callIdElement)
                    ? callIdElement.GetString()
                    : item.TryGetProperty("id", out var idElement)
                        ? idElement.GetString()
                        : null,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                    ["arguments"] = item.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.GetString() ?? string.Empty : string.Empty
                }
            });
        }

        return toolCalls;
    }

    /// <summary>
    /// Extracts concatenated output text from the response output array.
    /// </summary>
    /// <param name="response">The response JSON element.</param>
    /// <returns>The combined output text.</returns>
    internal static string ExtractOutputText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            if (!string.Equals(type, "message", StringComparison.Ordinal))
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partTypeElement)
                    && string.Equals(partTypeElement.GetString(), "output_text", StringComparison.Ordinal)
                    && part.TryGetProperty("text", out var textElement))
                {
                    parts.Add(textElement.GetString() ?? string.Empty);
                }
            }
        }

        return string.Join(string.Empty, parts);
    }

    /// <summary>
    /// Extracts concatenated reasoning summary text from the response output array.
    /// </summary>
    /// <param name="response">The response JSON element.</param>
    /// <returns>The combined reasoning text.</returns>
    internal static string ExtractReasoningText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in outputElement.EnumerateArray())
        {
            if (!TryGetReasoningSummaryText(item, out var reasoningText))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(reasoningText))
            {
                parts.Add(reasoningText);
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Attempts to extract reasoning summary text from a response output item.
    /// </summary>
    /// <param name="element">The output item element.</param>
    /// <param name="reasoningText">The extracted reasoning text.</param>
    /// <returns><see langword="true"/> if extraction succeeded; otherwise <see langword="false"/>.</returns>
    internal static bool TryGetReasoningSummaryText(JsonElement element, out string reasoningText)
    {
        reasoningText = string.Empty;
        if (!element.TryGetProperty("type", out var typeElement) || !string.Equals(typeElement.GetString(), "reasoning", StringComparison.Ordinal))
        {
            return false;
        }

        if (!element.TryGetProperty("summary", out var summaryElement) || summaryElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var item in summaryElement.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var summaryTypeElement)
                && string.Equals(summaryTypeElement.GetString(), "summary_text", StringComparison.Ordinal)
                && item.TryGetProperty("text", out var textElement))
            {
                parts.Add(textElement.GetString() ?? string.Empty);
            }
        }

        reasoningText = string.Join("\n", parts);
        return !string.IsNullOrWhiteSpace(reasoningText);
    }
}
