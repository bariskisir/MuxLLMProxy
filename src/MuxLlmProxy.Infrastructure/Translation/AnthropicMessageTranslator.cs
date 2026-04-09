using System.Text;
using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Contracts;

namespace MuxLlmProxy.Infrastructure.Translation;

/// <summary>
/// Translates Anthropic messages payloads to and from OpenAI-compatible payloads.
/// </summary>
public sealed class AnthropicMessageTranslator : IMessageTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts an Anthropic messages request into an OpenAI-compatible request payload.
    /// </summary>
    /// <param name="anthropicJson">The source Anthropic JSON payload.</param>
    /// <returns>The translated OpenAI-compatible JSON payload.</returns>
    public byte[] ToOpenAiRequest(byte[] anthropicJson)
    {
        using var probeDocument = JsonDocument.Parse(anthropicJson);
        var probeRoot = probeDocument.RootElement;
        var looksAnthropic = probeRoot.TryGetProperty("thinking", out _)
            || probeRoot.TryGetProperty("system", out _)
            || LooksAnthropicToolPayload(probeRoot)
            || LooksAnthropicMessagePayload(probeRoot);

        OpenAiChatRequest? openAiRequest = null;
        try
        {
            openAiRequest = JsonSerializer.Deserialize<OpenAiChatRequest>(anthropicJson, SerializerOptions);
        }
        catch (JsonException)
        {
            openAiRequest = null;
        }

        if (!looksAnthropic
            && openAiRequest is not null
            && !string.IsNullOrWhiteSpace(openAiRequest.Model)
            && openAiRequest.Messages is not null)
        {
            return JsonSerializer.SerializeToUtf8Bytes(openAiRequest, SerializerOptions);
        }

        var request = JsonSerializer.Deserialize<AnthropicMessagesRequest>(anthropicJson, SerializerOptions)
            ?? throw new JsonException("The Anthropic request payload is invalid.");

        var messages = new List<OpenAiMessage>();
        var systemText = FlattenContent(request.System);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = systemText });
        }

        messages.AddRange(TranslateAnthropicMessages(request.Messages));

        var translated = new OpenAiChatRequest
        {
            Model = request.Model,
            Messages = messages,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Stream = request.Stream,
            Tools = request.Tools?
                .Select(tool => CreateOpenAiTool(tool))
                .Where(tool => tool is not null)
                .Cast<OpenAiTool>()
                .ToArray(),
            ToolChoice = request.ToolChoice,
            ReasoningSummary = request.Thinking is null ? null : "detailed",
            ReasoningEffort = MapReasoningEffort(request.Thinking),
            Verbosity = request.Thinking is null ? null : "medium"
        };

        return JsonSerializer.SerializeToUtf8Bytes(translated, SerializerOptions);
    }

    /// <summary>
    /// Converts an OpenAI-compatible non-streaming response into an Anthropic response payload.
    /// </summary>
    public byte[] ToAnthropicResponse(byte[] openAiJson, string model)
    {
        using var document = JsonDocument.Parse(openAiJson);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var toolUseBlocks = ExtractToolUseBlocks(message);
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? MapStopReason(finishReasonElement.GetString())
            : "end_turn";

        if (toolUseBlocks.Count > 0)
        {
            finishReason = "tool_use";
        }

        var promptTokens = root.TryGetProperty("usage", out var usageElement) && usageElement.TryGetProperty("prompt_tokens", out var promptTokensElement)
            ? promptTokensElement.GetInt32()
            : 0;

        var completionTokens = root.TryGetProperty("usage", out usageElement) && usageElement.TryGetProperty("completion_tokens", out var completionTokensElement)
            ? completionTokensElement.GetInt32()
            : 0;

        var blocks = new List<object>();
        if (!string.IsNullOrEmpty(content))
        {
            blocks.Add(new { type = "text", text = content });
        }

        blocks.AddRange(toolUseBlocks);

        var payload = new
        {
            id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : "msg_proxy",
            type = "message",
            role = "assistant",
            model,
            content = blocks,
            stop_reason = finishReason,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = promptTokens,
                output_tokens = completionTokens
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    /// <summary>
    /// Converts an OpenAI-compatible SSE stream payload into Anthropic SSE events.
    /// </summary>
    public byte[] ToAnthropicStream(byte[] openAiStreamBytes, string model)
    {
        var messageId = TryExtractResponseIdFromStream(openAiStreamBytes) ?? $"msg_{Guid.NewGuid():N}";
        var builder = new StringBuilder();
        builder.AppendLine("event: message_start");
        builder.AppendLine($"data: {{\"type\":\"message_start\",\"message\":{{\"id\":{JsonSerializer.Serialize(messageId)},\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{model}\",\"content\":[]}}}}");
        builder.AppendLine();

        var startedTextBlock = false;
        var startedToolBlock = false;
        var toolBlockIndex = 2;
        var stopReason = "end_turn";
        var lines = Encoding.UTF8.GetString(openAiStreamBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choicesElement[0];
            var delta = choice.TryGetProperty("delta", out var deltaElement) ? deltaElement : default;
            if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("content", out var textElement))
            {
                var text = textElement.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    if (!startedTextBlock)
                    {
                        builder.AppendLine("event: content_block_start");
                        builder.AppendLine("data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
                        builder.AppendLine();
                        startedTextBlock = true;
                    }

                    builder.AppendLine("event: content_block_delta");
                    builder.AppendLine($"data: {{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"text_delta\",\"text\":{JsonSerializer.Serialize(text)}}}}}");
                    builder.AppendLine();
                }
            }

            if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCallsElement.EnumerateArray())
                {
                    var toolId = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : string.Empty;
                    var toolName = toolCall.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object && functionElement.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString()
                        : string.Empty;

                    if (!startedToolBlock)
                    {
                        builder.AppendLine("event: content_block_start");
                        builder.AppendLine($"data: {{\"type\":\"content_block_start\",\"index\":{toolBlockIndex},\"content_block\":{{\"type\":\"tool_use\",\"id\":{JsonSerializer.Serialize(toolId)},\"name\":{JsonSerializer.Serialize(toolName)},\"input\":{{}}}}}}");
                        builder.AppendLine();
                        startedToolBlock = true;
                        stopReason = "tool_use";
                    }

                    if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var argumentsElement))
                    {
                        var argumentsDelta = DecodeJsonStringOrRaw(argumentsElement);
                        if (!string.IsNullOrEmpty(argumentsDelta))
                        {
                            builder.AppendLine("event: content_block_delta");
                            builder.AppendLine($"data: {{\"type\":\"content_block_delta\",\"index\":{toolBlockIndex},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonSerializer.Serialize(argumentsDelta)}}}}}");
                            builder.AppendLine();
                        }
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReasonElement) && finishReasonElement.ValueKind == JsonValueKind.String)
            {
                stopReason = MapStopReason(finishReasonElement.GetString());
            }
        }

        if (startedTextBlock)
        {
            builder.AppendLine("event: content_block_stop");
            builder.AppendLine("data: {\"type\":\"content_block_stop\",\"index\":1}");
            builder.AppendLine();
        }

        if (startedToolBlock)
        {
            builder.AppendLine("event: content_block_stop");
            builder.AppendLine($"data: {{\"type\":\"content_block_stop\",\"index\":{toolBlockIndex}}}");
            builder.AppendLine();
        }

        builder.AppendLine("event: message_delta");
        builder.AppendLine($"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":{JsonSerializer.Serialize(stopReason)},\"stop_sequence\":null}},\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}");
        builder.AppendLine();
        builder.AppendLine("event: message_stop");
        builder.AppendLine("data: {\"type\":\"message_stop\"}");
        builder.AppendLine();

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string? TryExtractResponseIdFromStream(byte[] openAiStreamBytes)
    {
        foreach (var rawLine in Encoding.UTF8.GetString(openAiStreamBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)
                    || !string.Equals(typeElement.GetString(), "response.created", StringComparison.Ordinal))
                {
                    continue;
                }

                var response = root.TryGetProperty("response", out var responseElement) ? responseElement : root;
                if (response.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    return idElement.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static OpenAiTool? CreateOpenAiTool(AnthropicTool tool)
    {
        var name = tool.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = tool.Function?.Name;
        }

        if (string.IsNullOrWhiteSpace(name) && !string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase))
        {
            name = tool.Type;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new OpenAiTool
        {
            Type = "function",
            Function = new OpenAiFunctionDefinition
            {
                Name = name,
                Description = tool.Description ?? tool.Function?.Description,
                Parameters = tool.InputSchema ?? tool.Function?.Parameters
            }
        };
    }

    private static List<object> ExtractToolUseBlocks(JsonElement message)
    {
        var blocks = new List<object>();
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var argumentsText = functionElement.TryGetProperty("arguments", out var argumentsElement)
                ? DecodeJsonStringOrRaw(argumentsElement)
                : string.Empty;

            blocks.Add(new
            {
                type = "tool_use",
                id = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : string.Empty,
                name = functionElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : string.Empty,
                input = ParseToolInputOrFallback(argumentsText)
            });
        }

        return blocks;
    }

    private static string FlattenAnthropicContent(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return contentElement.ToString();
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                if (string.Equals(type, "text", StringComparison.Ordinal) && item.TryGetProperty("text", out var textElement))
                {
                    parts.Add(textElement.GetString() ?? string.Empty);
                    continue;
                }
            }

            parts.Add(item.ToString());
        }

        return string.Join("\n", parts);
    }

    private static IReadOnlyList<(string ToolUseId, string Content)> ExtractToolResults(JsonElement contentElement)
    {
        var results = new List<(string ToolUseId, string Content)>();
        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), "tool_result", StringComparison.Ordinal)
                && item.TryGetProperty("tool_use_id", out var toolUseIdElement))
            {
                var toolUseId = toolUseIdElement.GetString();
                if (string.IsNullOrWhiteSpace(toolUseId))
                {
                    continue;
                }

                var content = item.TryGetProperty("content", out var resultContentElement)
                    ? FlattenAnthropicContent(resultContentElement)
                    : string.Empty;

                results.Add((toolUseId, content));
            }
        }

        return results;
    }

    private static IReadOnlyList<OpenAiMessage> TranslateAnthropicMessages(IReadOnlyList<AnthropicMessage> messages)
    {
        var translated = new List<OpenAiMessage>();
        foreach (var message in messages)
        {
            if (message.Content is JsonElement element)
            {
                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    var toolResults = ExtractToolResults(element);
                    if (toolResults.Count > 0)
                    {
                        foreach (var toolResult in toolResults)
                        {
                            translated.Add(new OpenAiMessage
                            {
                                Role = "tool",
                                ToolCallId = toolResult.ToolUseId,
                                Content = toolResult.Content
                            });
                        }

                        var userText = ExtractUserTextContent(element);
                        if (!string.IsNullOrWhiteSpace(userText))
                        {
                            translated.Add(new OpenAiMessage
                            {
                                Role = "user",
                                Content = userText
                            });
                        }

                        continue;
                    }
                }

                if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && TryTranslateAssistantToolUseMessage(element, out var assistantMessage))
                {
                    translated.Add(assistantMessage);
                    continue;
                }
            }

            translated.Add(new OpenAiMessage
            {
                Role = message.Role,
                Content = FlattenContent(message.Content)
            });
        }

        return translated;
    }

    private static string ExtractUserTextContent(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return contentElement.ToString();
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                parts.Add(item.ToString());
                continue;
            }

            if (item.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), "tool_result", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.TryGetProperty("text", out var textElement))
            {
                var text = PromptTextSanitizer.Sanitize(textElement.GetString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }

                continue;
            }

            parts.Add(item.ToString());
        }

        return PromptTextSanitizer.Sanitize(string.Join("\n", parts));
    }

    private static bool TryTranslateAssistantToolUseMessage(JsonElement contentElement, out OpenAiMessage message)
    {
        message = null!;
        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var textParts = new List<string>();
        var toolCalls = new List<object>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            if (string.Equals(type, "text", StringComparison.Ordinal) && item.TryGetProperty("text", out var textElement))
            {
                var text = PromptTextSanitizer.Sanitize(textElement.GetString() ?? string.Empty).Trim();
                if (!ShouldDropAssistantPlanningText(text))
                {
                    textParts.Add(text);
                }
                continue;
            }

            if (!string.Equals(type, "tool_use", StringComparison.Ordinal))
            {
                continue;
            }

            var inputJson = item.TryGetProperty("input", out var inputElement)
                ? inputElement.GetRawText()
                : "{}";
            toolCalls.Add(new
            {
                id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : string.Empty,
                type = "function",
                function = new
                {
                    name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : string.Empty,
                    arguments = inputJson
                }
            });
        }

        if (toolCalls.Count == 0)
        {
            return false;
        }

        message = new OpenAiMessage
        {
            Role = "assistant",
            Content = textParts.Count > 0 ? string.Join("\n", textParts) : string.Empty,
            ToolCalls = toolCalls
        };

        return true;
    }

    private static string FlattenContent(object? content)
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

    private static string FlattenJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return PromptTextSanitizer.Sanitize(element.GetString() ?? string.Empty);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return element.ToString();
        }

        var parts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textProperty))
                {
                    var text = PromptTextSanitizer.Sanitize(textProperty.GetString() ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }
            }

        return PromptTextSanitizer.Sanitize(string.Join("\n", parts));
    }

    private static bool ShouldDropAssistantPlanningText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (PromptTextSanitizer.ContainsClientHarness(text))
        {
            return true;
        }

        return text.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("# Todos", StringComparison.Ordinal)
            || text.StartsWith("Preparing for", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Deciding on", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I need to", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I’m ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I'm ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("todowrite", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TaskCreate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Plan mode", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeJsonStringOrRaw(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
    }

    private static object ParseToolInputOrFallback(string argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<object>(argumentsText, SerializerOptions) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>
            {
                ["raw_arguments"] = argumentsText
            };
        }
    }

    private static string? MapReasoningEffort(AnthropicThinkingConfig? thinking)
    {
        if (thinking is null || string.Equals(thinking.Type, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return thinking.BudgetTokens switch
        {
            >= 2048 => "high",
            >= 512 => "medium",
            _ => "low"
        };
    }

    private static string MapStopReason(string? finishReason)
    {
        return finishReason switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            _ => "end_turn"
        };
    }

    private static bool LooksAnthropicToolPayload(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tool in toolsElement.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object
                && tool.TryGetProperty("input_schema", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksAnthropicMessagePayload(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messagesElement.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var contentElement))
            {
                continue;
            }

            if (LooksAnthropicContentBlock(contentElement))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksAnthropicContentBlock(JsonElement contentElement)
    {
        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            if (string.Equals(type, "text", StringComparison.Ordinal)
                || string.Equals(type, "tool_use", StringComparison.Ordinal)
                || string.Equals(type, "tool_result", StringComparison.Ordinal)
                || string.Equals(type, "thinking", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
