using System.Text;
using System.Text.Json;

namespace MuxLlmProxy.Infrastructure.Translation;

internal static class AnthropicStreamConverter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static byte[] ConvertOpenAiStream(byte[] openAiStreamBytes, string model)
    {
        var messageId = TryExtractResponseId(openAiStreamBytes) ?? $"msg_{Guid.NewGuid():N}";
        var inputTokens = 0;
        var outputTokens = 0;
        var finishReason = "end_turn";
        var sse = new AnthropicSseBuilder(messageId, model, inputTokens);
        var output = new StringBuilder();
        output.Append(sse.MessageStart());

        var thinkParser = new AnthropicThinkingParser();
        var heuristicParser = new HeuristicToolParser();
        var lines = Encoding.UTF8.GetString(openAiStreamBytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usageElement))
            {
                inputTokens = ReadInt(usageElement, "prompt_tokens") ?? inputTokens;
                outputTokens = ReadInt(usageElement, "completion_tokens") ?? outputTokens;
            }

            if (!root.TryGetProperty("choices", out var choicesElement)
                || choicesElement.ValueKind != JsonValueKind.Array
                || choicesElement.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choicesElement[0];
            if (choice.TryGetProperty("finish_reason", out var finishElement) && finishElement.ValueKind == JsonValueKind.String)
            {
                finishReason = MapStopReason(finishElement.GetString());
            }

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
            {
                var reasoning = reasoningElement.GetString();
                if (!string.IsNullOrEmpty(reasoning))
                {
                    AppendAll(output, sse.EnsureThinkingBlock());
                    output.Append(sse.EmitThinkingDelta(reasoning));
                }
            }

            if (delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
            {
                var content = contentElement.GetString();
                if (!string.IsNullOrEmpty(content))
                {
                    EmitTextAndHeuristicTools(output, sse, thinkParser, heuristicParser, content);
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                AppendAll(output, sse.CloseContentBlocks());
                foreach (var toolCall in toolCallsElement.EnumerateArray())
                {
                    ProcessToolCall(output, sse, toolCall);
                }
            }
        }

        var remaining = thinkParser.Flush();
        if (remaining is not null)
        {
            if (remaining.Type == ContentChunkType.Thinking)
            {
                AppendAll(output, sse.EnsureThinkingBlock());
                output.Append(sse.EmitThinkingDelta(remaining.Content));
            }
            else
            {
                AppendAll(output, sse.EnsureTextBlock());
                output.Append(sse.EmitTextDelta(remaining.Content));
            }
        }

        foreach (var toolUse in heuristicParser.Flush())
        {
            EmitHeuristicToolUse(output, sse, toolUse);
        }

        if (!sse.HasContentBlocks)
        {
            AppendAll(output, sse.EnsureTextBlock());
            output.Append(sse.EmitTextDelta(" "));
        }
        else if (!sse.HasStartedTool && !sse.HasMeaningfulText && sse.HasMeaningfulThinking)
        {
            AppendAll(output, sse.EnsureTextBlock());
            output.Append(sse.EmitTextDelta(" "));
        }

        FlushTaskArgumentBuffers(output, sse);
        AppendAll(output, sse.CloseAllBlocks());

        output.Append(new AnthropicSseBuilder(messageId, model, inputTokens).MessageDelta(finishReason, outputTokens > 0 ? outputTokens : sse.EstimateOutputTokens()));
        output.Append(new AnthropicSseBuilder(messageId, model, inputTokens).MessageStop());
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void EmitTextAndHeuristicTools(
        StringBuilder output,
        AnthropicSseBuilder sse,
        AnthropicThinkingParser thinkParser,
        HeuristicToolParser heuristicParser,
        string content)
    {
        foreach (var part in thinkParser.Feed(content))
        {
            if (part.Type == ContentChunkType.Thinking)
            {
                AppendAll(output, sse.EnsureThinkingBlock());
                output.Append(sse.EmitThinkingDelta(part.Content));
                continue;
            }

            var parsed = heuristicParser.Feed(part.Content);
            if (!string.IsNullOrEmpty(parsed.Text))
            {
                AppendAll(output, sse.EnsureTextBlock());
                output.Append(sse.EmitTextDelta(parsed.Text));
            }

            foreach (var toolUse in parsed.ToolUses)
            {
                EmitHeuristicToolUse(output, sse, toolUse);
            }
        }
    }

    private static void EmitHeuristicToolUse(StringBuilder output, AnthropicSseBuilder sse, Dictionary<string, object?> toolUse)
    {
        AppendAll(output, sse.CloseContentBlocks());
        var blockIndex = NextSyntheticToolIndex(sse);
        var toolId = toolUse.TryGetValue("id", out var id) ? id?.ToString() ?? $"toolu_{Guid.NewGuid():N}" : $"toolu_{Guid.NewGuid():N}";
        var name = toolUse.TryGetValue("name", out var rawName) ? rawName?.ToString() ?? "tool_call" : "tool_call";
        output.Append(sse.StartToolBlock(blockIndex, toolId, name));
        var input = toolUse.TryGetValue("input", out var rawInput) ? rawInput : new Dictionary<string, object?>();
        output.Append(sse.EmitToolDelta(blockIndex, JsonSerializer.Serialize(input, SerializerOptions)));
        AppendAll(output, sse.CloseAllBlocks());
    }

    private static void ProcessToolCall(StringBuilder output, AnthropicSseBuilder sse, JsonElement toolCall)
    {
        var toolIndex = toolCall.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : NextSyntheticToolIndex(sse);
        if (toolIndex < 0)
        {
            toolIndex = NextSyntheticToolIndex(sse);
        }

        var state = sse.EnsureToolState(toolIndex);
        if (toolCall.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            state.ToolId = idElement.GetString() ?? state.ToolId;
        }

        var function = toolCall.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object
            ? functionElement
            : default;

        if (function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
        {
            MergeToolName(state, nameElement.GetString() ?? string.Empty);
        }

        var arguments = function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("arguments", out var argumentsElement)
            && argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString() ?? string.Empty
                : string.Empty;

        if (!state.Started && !string.IsNullOrWhiteSpace(state.Name))
        {
            var toolId = !string.IsNullOrWhiteSpace(state.ToolId) ? state.ToolId : $"tool_{Guid.NewGuid():N}";
            output.Append(sse.StartToolBlock(toolIndex, toolId, state.Name.Trim()));
            if (!string.IsNullOrEmpty(state.PreStartArguments))
            {
                EmitToolArgumentDelta(output, sse, toolIndex, state.PreStartArguments);
                state.PreStartArguments = string.Empty;
            }
        }

        if (string.IsNullOrEmpty(arguments))
        {
            return;
        }

        if (!state.Started)
        {
            state.PreStartArguments += arguments;
            return;
        }

        EmitToolArgumentDelta(output, sse, toolIndex, arguments);
    }

    private static void EmitToolArgumentDelta(StringBuilder output, AnthropicSseBuilder sse, int toolIndex, string arguments)
    {
        var state = sse.EnsureToolState(toolIndex);
        if (state.Name != "Task")
        {
            output.Append(sse.EmitToolDelta(toolIndex, arguments));
            return;
        }

        if (state.TaskArgumentsEmitted)
        {
            return;
        }

        state.TaskArgumentBuffer.Append(arguments);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(state.TaskArgumentBuffer.ToString(), SerializerOptions);
            if (parsed is null)
            {
                return;
            }

            parsed["run_in_background"] = false;
            state.TaskArgumentsEmitted = true;
            state.TaskArgumentBuffer.Clear();
            output.Append(sse.EmitToolDelta(toolIndex, JsonSerializer.Serialize(parsed, SerializerOptions)));
        }
        catch (JsonException)
        {
        }
    }

    private static void FlushTaskArgumentBuffers(StringBuilder output, AnthropicSseBuilder sse)
    {
        foreach (var (toolIndex, state) in sse.ToolStates)
        {
            if (state.TaskArgumentBuffer.Length == 0 || state.TaskArgumentsEmitted)
            {
                continue;
            }

            var outJson = "{}";
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(state.TaskArgumentBuffer.ToString(), SerializerOptions)
                    ?? new Dictionary<string, object?>();
                parsed["run_in_background"] = false;
                outJson = JsonSerializer.Serialize(parsed, SerializerOptions);
            }
            catch (JsonException)
            {
            }

            state.TaskArgumentsEmitted = true;
            state.TaskArgumentBuffer.Clear();
            output.Append(sse.EmitToolDelta(toolIndex, outJson));
        }
    }

    private static string? TryExtractResponseId(byte[] openAiStreamBytes)
    {
        foreach (var rawLine in Encoding.UTF8.GetString(openAiStreamBytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]")
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
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

    private static void MergeToolName(StreamingToolCallState state, string incomingName)
    {
        if (string.IsNullOrEmpty(incomingName))
        {
            return;
        }

        if (string.IsNullOrEmpty(state.Name) || incomingName.StartsWith(state.Name, StringComparison.Ordinal))
        {
            state.Name = incomingName;
        }
        else if (!state.Name.StartsWith(incomingName, StringComparison.Ordinal))
        {
            state.Name += incomingName;
        }
    }

    private static int NextSyntheticToolIndex(AnthropicSseBuilder sse)
    {
        return sse.ToolStates.Count == 0 ? 0 : sse.ToolStates.Keys.Max() + 1;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : null;
    }

    private static void AppendAll(StringBuilder output, IEnumerable<string> events)
    {
        foreach (var item in events)
        {
            output.Append(item);
        }
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
