using System.Text.Json;

namespace MuxLlmProxy.Infrastructure.Translation;

internal enum ReasoningReplayMode
{
    Disabled,
    ThinkTags,
    ReasoningContent
}

internal static class AnthropicToOpenAiConverter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static byte[] ConvertRequest(byte[] requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        if (!LooksAnthropic(root))
        {
            return requestJson;
        }

        var messages = ConvertMessages(root.GetProperty("messages"), ReasoningReplayMode.ThinkTags);
        if (root.TryGetProperty("system", out var systemElement)
            && TryConvertSystemPrompt(systemElement, out var systemMessage))
        {
            messages.Insert(0, systemMessage);
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = AnthropicContent.GetString(root, "model"),
            ["messages"] = messages
        };

        CopyIfPresent(root, body, "max_tokens");
        CopyIfPresent(root, body, "temperature");
        CopyIfPresent(root, body, "top_p");
        if (root.TryGetProperty("stream", out var streamElement))
        {
            body["stream"] = AnthropicContent.JsonValue(streamElement);
        }
        else
        {
            body["stream"] = true;
        }

        if (root.TryGetProperty("stop_sequences", out var stopSequences) && stopSequences.ValueKind == JsonValueKind.Array)
        {
            body["stop"] = AnthropicContent.JsonValue(stopSequences);
        }

        if (root.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
        {
            body["tools"] = ConvertTools(toolsElement);
        }

        if (root.TryGetProperty("tool_choice", out var toolChoiceElement))
        {
            body["tool_choice"] = ConvertToolChoice(toolChoiceElement);
        }

        if (root.TryGetProperty("thinking", out var thinkingElement)
            && thinkingElement.ValueKind == JsonValueKind.Object
            && !string.Equals(AnthropicContent.GetString(thinkingElement, "type"), "disabled", StringComparison.OrdinalIgnoreCase))
        {
            body["reasoning_effort"] = MapReasoningEffort(thinkingElement);
            body["reasoningSummary"] = "detailed";
            body["verbosity"] = "medium";
        }

        return JsonSerializer.SerializeToUtf8Bytes(body, SerializerOptions);
    }

    private static List<Dictionary<string, object?>> ConvertMessages(JsonElement messagesElement, ReasoningReplayMode reasoningReplay)
    {
        var result = new List<Dictionary<string, object?>>();
        PendingAfterTools? pending = null;

        foreach (var message in messagesElement.EnumerateArray())
        {
            var role = AnthropicContent.GetString(message, "role");
            if (!message.TryGetProperty("content", out var content))
            {
                continue;
            }

            var reasoningContent = AnthropicContent.GetString(message, "reasoning_content");

            if (role == "assistant" && content.ValueKind == JsonValueKind.Array)
            {
                if (pending is not null && pending.NeedsDeferred)
                {
                    result.AddRange(DeferredPostToolToMessages(pending));
                    pending.DeferredEmitted = true;
                    pending = null;
                }

                var blocks = content.EnumerateArray().ToArray();
                var firstToolIndex = IndexFirstToolUse(blocks);
                if (firstToolIndex is not null)
                {
                    foreach (var block in blocks)
                    {
                        if (AnthropicContent.GetBlockType(block) != "tool_use")
                        {
                            AssertNoForbiddenAssistantBlock(block);
                        }
                    }

                    var converted = ConvertAssistantMessageWithSplit(blocks, firstToolIndex.Value, reasoningContent, reasoningReplay);
                    result.AddRange(converted.Messages);
                    pending = converted.Pending;
                }
                else
                {
                    foreach (var block in blocks)
                    {
                        AssertNoForbiddenAssistantBlock(block);
                    }

                    result.AddRange(ConvertAssistantMessage(blocks, reasoningContent, reasoningReplay));
                }

                continue;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                if (role == "user" && pending is not null && pending.NeedsDeferred)
                {
                    result.AddRange(DeferredPostToolToMessages(pending));
                    pending.DeferredEmitted = true;
                    pending = null;
                }

                var converted = new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["content"] = content.GetString() ?? string.Empty
                };
                if (role == "assistant" && !string.IsNullOrWhiteSpace(reasoningContent))
                {
                    converted["content"] = reasoningReplay switch
                    {
                        ReasoningReplayMode.ReasoningContent => content.GetString() ?? string.Empty,
                        ReasoningReplayMode.ThinkTags => JoinNonEmpty("\n\n", ThinkTag(reasoningContent), content.GetString() ?? string.Empty),
                        _ => content.GetString() ?? string.Empty
                    };
                    if (reasoningReplay == ReasoningReplayMode.ReasoningContent)
                    {
                        converted["reasoning_content"] = reasoningContent;
                    }
                }

                result.Add(converted);
                continue;
            }

            if (content.ValueKind == JsonValueKind.Array && role == "user")
            {
                var blocks = content.EnumerateArray().ToArray();
                if (pending is not null && pending.NeedsDeferred)
                {
                    if (pending.RemainingToolIds.Count == 0)
                    {
                        result.AddRange(DeferredPostToolToMessages(pending));
                        pending.DeferredEmitted = true;
                        pending = null;
                        result.AddRange(ConvertUserMessage(blocks));
                    }
                    else
                    {
                        var injected = ConvertUserMessageWithInjection(blocks, pending);
                        result.AddRange(injected.Messages);
                        if (injected.ClearedPending)
                        {
                            pending = null;
                        }
                    }
                }
                else
                {
                    result.AddRange(ConvertUserMessage(blocks));
                }

                continue;
            }

            if (role == "user" && pending is not null && pending.NeedsDeferred)
            {
                result.AddRange(DeferredPostToolToMessages(pending));
                pending.DeferredEmitted = true;
                pending = null;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = content.ToString()
            });
        }

        if (pending is not null && pending.NeedsDeferred)
        {
            result.AddRange(DeferredPostToolToMessages(pending));
        }

        return result;
    }

    private static (IReadOnlyList<Dictionary<string, object?>> Messages, PendingAfterTools? Pending) ConvertAssistantMessageWithSplit(
        JsonElement[] content,
        int firstToolIndex,
        string? reasoningContent,
        ReasoningReplayMode reasoningReplay)
    {
        var pre = content.Take(firstToolIndex).ToArray();
        var toolCalls = IterToolUsesInOrder(content);
        if (toolCalls.Count == 0)
        {
            return (ConvertAssistantMessage(content, reasoningContent, reasoningReplay), null);
        }

        Dictionary<string, object?> preMessage;
        if (pre.Length == 0)
        {
            preMessage = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.Empty
            };
            if (reasoningReplay == ReasoningReplayMode.ReasoningContent && !string.IsNullOrWhiteSpace(reasoningContent))
            {
                preMessage["reasoning_content"] = reasoningContent;
            }
        }
        else
        {
            preMessage = ConvertAssistantMessage(pre, reasoningContent, reasoningReplay)[0];
        }

        preMessage["tool_calls"] = toolCalls;
        if (toolCalls.Count > 0 && Equals(preMessage["content"], " "))
        {
            preMessage["content"] = string.Empty;
        }

        var deferredBlocks = content
            .Skip(firstToolIndex + 1)
            .Where(block => AnthropicContent.GetBlockType(block) != "tool_use")
            .ToArray();

        PendingAfterTools? pending = null;
        if (deferredBlocks.Length > 0)
        {
            pending = new PendingAfterTools(
                toolCalls
                    .Select(call => call.TryGetValue("id", out var value) ? value?.ToString() : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal),
                deferredBlocks,
                reasoningContent,
                reasoningReplay);
        }

        return (new[] { preMessage }, pending);
    }

    private static List<Dictionary<string, object?>> ConvertAssistantMessage(JsonElement[] content, string? reasoningContent, ReasoningReplayMode reasoningReplay)
    {
        var contentParts = new List<string>();
        var thinkingParts = new List<string>();
        var toolCalls = new List<Dictionary<string, object?>>();

        foreach (var block in content)
        {
            var blockType = AnthropicContent.GetBlockType(block);
            switch (blockType)
            {
                case "text":
                    contentParts.Add(AnthropicContent.GetString(block, "text"));
                    break;
                case "thinking":
                    if (reasoningReplay == ReasoningReplayMode.Disabled)
                    {
                        break;
                    }

                    var thinking = AnthropicContent.GetString(block, "thinking");
                    if (reasoningReplay == ReasoningReplayMode.ThinkTags)
                    {
                        contentParts.Add(ThinkTag(thinking));
                    }
                    else if (string.IsNullOrWhiteSpace(reasoningContent))
                    {
                        thinkingParts.Add(thinking);
                    }

                    break;
                case "redacted_thinking":
                    break;
                case "tool_use":
                    toolCalls.Add(CreateToolCall(block));
                    break;
                default:
                    AssertNoForbiddenAssistantBlock(block);
                    break;
            }
        }

        var contentString = string.Join("\n\n", contentParts.Where(part => part.Length > 0));
        if (string.IsNullOrEmpty(contentString) && toolCalls.Count == 0)
        {
            contentString = " ";
        }

        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = contentString
        };

        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        if (reasoningReplay == ReasoningReplayMode.ReasoningContent)
        {
            var replay = !string.IsNullOrWhiteSpace(reasoningContent)
                ? reasoningContent
                : string.Join("\n", thinkingParts);
            if (!string.IsNullOrWhiteSpace(replay))
            {
                message["reasoning_content"] = replay;
            }
        }

        return [message];
    }

    private static List<Dictionary<string, object?>> ConvertUserMessage(JsonElement[] content)
    {
        var result = new List<Dictionary<string, object?>>();
        var textParts = new List<string>();

        void FlushText()
        {
            if (textParts.Count == 0)
            {
                return;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = string.Join("\n", textParts)
            });
            textParts.Clear();
        }

        foreach (var block in content)
        {
            var blockType = AnthropicContent.GetBlockType(block);
            switch (blockType)
            {
                case "text":
                    textParts.Add(AnthropicContent.GetString(block, "text"));
                    break;
                case "image":
                    throw new InvalidOperationException("User image blocks are not supported for OpenAI chat conversion.");
                case "tool_result":
                    FlushText();
                    result.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = AnthropicContent.GetString(block, "tool_use_id"),
                        ["content"] = block.TryGetProperty("content", out var toolContent)
                            ? AnthropicContent.SerializeToolResultContent(toolContent)
                            : string.Empty
                    });
                    break;
            }
        }

        FlushText();
        return result;
    }

    private static (IReadOnlyList<Dictionary<string, object?>> Messages, bool ClearedPending) ConvertUserMessageWithInjection(JsonElement[] content, PendingAfterTools pending)
    {
        var result = new List<Dictionary<string, object?>>();
        var textParts = new List<string>();
        var cleared = false;

        void FlushText()
        {
            if (textParts.Count == 0)
            {
                return;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = string.Join("\n", textParts)
            });
            textParts.Clear();
        }

        foreach (var block in content)
        {
            var blockType = AnthropicContent.GetBlockType(block);
            if (blockType == "text")
            {
                textParts.Add(AnthropicContent.GetString(block, "text"));
                continue;
            }

            if (blockType == "image")
            {
                throw new InvalidOperationException("User image blocks are not supported for OpenAI chat conversion.");
            }

            if (blockType != "tool_result")
            {
                continue;
            }

            FlushText();
            var toolUseId = AnthropicContent.GetString(block, "tool_use_id");
            result.Add(new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = toolUseId,
                ["content"] = block.TryGetProperty("content", out var toolContent)
                    ? AnthropicContent.SerializeToolResultContent(toolContent)
                    : string.Empty
            });

            pending.RemainingToolIds.Remove(toolUseId);
            if (pending.RemainingToolIds.Count == 0)
            {
                result.AddRange(DeferredPostToolToMessages(pending));
                pending.DeferredEmitted = true;
                cleared = true;
            }
        }

        FlushText();
        return (result, cleared);
    }

    private static List<Dictionary<string, object?>> DeferredPostToolToMessages(PendingAfterTools pending)
    {
        return pending.DeferredBlocks.Length == 0
            ? []
            : ConvertAssistantMessage(pending.DeferredBlocks, pending.TopLevelReasoning, pending.ReasoningReplay);
    }

    private static List<Dictionary<string, object?>> IterToolUsesInOrder(IEnumerable<JsonElement> blocks)
    {
        return blocks
            .Where(block => AnthropicContent.GetBlockType(block) == "tool_use")
            .Select(CreateToolCall)
            .ToList();
    }

    private static Dictionary<string, object?> CreateToolCall(JsonElement block)
    {
        var input = block.TryGetProperty("input", out var inputElement)
            ? inputElement.GetRawText()
            : "{}";

        return new Dictionary<string, object?>
        {
            ["id"] = AnthropicContent.GetString(block, "id"),
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = AnthropicContent.GetString(block, "name"),
                ["arguments"] = input
            }
        };
    }

    private static int? IndexFirstToolUse(IReadOnlyList<JsonElement> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (AnthropicContent.GetBlockType(blocks[i]) == "tool_use")
            {
                return i;
            }
        }

        return null;
    }

    private static List<Dictionary<string, object?>> ConvertTools(JsonElement toolsElement)
    {
        var tools = new List<Dictionary<string, object?>>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            var name = AnthropicContent.GetString(tool, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (IsAnthropicServerTool(tool))
            {
                throw new InvalidOperationException(
                    $"OpenAI chat conversion does not support Anthropic server tool {name}.");
            }

            var schema = tool.TryGetProperty("input_schema", out var inputSchema)
                ? AnthropicContent.JsonValue(inputSchema)
                : new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>()
                };

            tools.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["description"] = AnthropicContent.GetString(tool, "description"),
                    ["parameters"] = schema
                }
            });
        }

        return tools;
    }

    private static object ConvertToolChoice(JsonElement toolChoice)
    {
        if (toolChoice.ValueKind != JsonValueKind.Object)
        {
            return AnthropicContent.JsonValue(toolChoice);
        }

        var choiceType = AnthropicContent.GetString(toolChoice, "type");
        if (choiceType == "tool")
        {
            var name = AnthropicContent.GetString(toolChoice, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = name
                    }
                };
            }
        }

        if (choiceType == "any")
        {
            return "required";
        }

        if (choiceType is "auto" or "none" or "required")
        {
            return choiceType;
        }

        return AnthropicContent.JsonValue(toolChoice);
    }

    private static bool TryConvertSystemPrompt(JsonElement system, out Dictionary<string, object?> message)
    {
        message = null!;
        if (system.ValueKind == JsonValueKind.String)
        {
            message = new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = system.GetString() ?? string.Empty
            };
            return true;
        }

        if (system.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var textParts = system.EnumerateArray()
            .Where(block => AnthropicContent.GetBlockType(block) == "text")
            .Select(block => AnthropicContent.GetString(block, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (textParts.Length == 0)
        {
            return false;
        }

        message = new Dictionary<string, object?>
        {
            ["role"] = "system",
            ["content"] = string.Join("\n\n", textParts).Trim()
        };
        return true;
    }

    private static void AssertNoForbiddenAssistantBlock(JsonElement block)
    {
        var blockType = AnthropicContent.GetBlockType(block);
        if (blockType == "image")
        {
            throw new InvalidOperationException("Assistant image blocks are not supported for OpenAI chat conversion.");
        }

        if (blockType is "server_tool_use" or "web_search_tool_result" or "web_fetch_tool_result")
        {
            throw new InvalidOperationException(
                $"OpenAI chat conversion does not support Anthropic server tool block {blockType} in assistant messages.");
        }
    }

    private static bool IsAnthropicServerTool(JsonElement tool)
    {
        var name = AnthropicContent.GetString(tool, "name");
        var type = AnthropicContent.GetString(tool, "type");
        return name is "web_search" or "web_fetch"
            || type.StartsWith("web_search", StringComparison.Ordinal)
            || type.StartsWith("web_fetch", StringComparison.Ordinal);
    }

    private static void CopyIfPresent(JsonElement root, IDictionary<string, object?> body, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element))
        {
            body[propertyName] = AnthropicContent.JsonValue(element);
        }
    }

    private static string MapReasoningEffort(JsonElement thinkingElement)
    {
        if (!thinkingElement.TryGetProperty("budget_tokens", out var budgetElement)
            || budgetElement.ValueKind != JsonValueKind.Number
            || !budgetElement.TryGetInt32(out var budget))
        {
            return "medium";
        }

        return budget switch
        {
            >= 2048 => "high",
            >= 512 => "medium",
            _ => "low"
        };
    }

    private static bool LooksAnthropic(JsonElement root)
    {
        return root.TryGetProperty("system", out _)
            || root.TryGetProperty("thinking", out _)
            || LooksAnthropicTools(root)
            || LooksAnthropicMessages(root);
    }

    private static bool LooksAnthropicTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return toolsElement.EnumerateArray()
            .Any(tool => tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("input_schema", out _));
    }

    private static bool LooksAnthropicMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messagesElement.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in contentElement.EnumerateArray())
            {
                var type = AnthropicContent.GetBlockType(block);
                if (type is "text" or "tool_use" or "tool_result" or "thinking" or "redacted_thinking")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ThinkTag(string reasoning)
    {
        return $"<think>\n{reasoning}\n</think>";
    }

    private static string JoinNonEmpty(string separator, params string[] values)
    {
        return string.Join(separator, values.Where(value => !string.IsNullOrEmpty(value)));
    }

    private sealed class PendingAfterTools
    {
        public PendingAfterTools(
            HashSet<string> remainingToolIds,
            JsonElement[] deferredBlocks,
            string? topLevelReasoning,
            ReasoningReplayMode reasoningReplay)
        {
            RemainingToolIds = remainingToolIds;
            DeferredBlocks = deferredBlocks;
            TopLevelReasoning = topLevelReasoning;
            ReasoningReplay = reasoningReplay;
        }

        public HashSet<string> RemainingToolIds { get; }

        public JsonElement[] DeferredBlocks { get; }

        public string? TopLevelReasoning { get; }

        public ReasoningReplayMode ReasoningReplay { get; }

        public bool DeferredEmitted { get; set; }

        public bool NeedsDeferred => DeferredBlocks.Length > 0 && !DeferredEmitted;
    }
}
