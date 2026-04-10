using System.Text;
using System.Text.Json;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Translates ChatGPT SSE response streams into OpenAI chat completion chunk format.
/// </summary>
internal static class ChatGptStreamTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts a buffered SSE response body into OpenAI chat completion chunks.
    /// </summary>
    /// <param name="body">The buffered upstream response body.</param>
    /// <param name="requestModel">The requested model identifier.</param>
    /// <returns>The translated chat completion stream bytes.</returns>
    public static byte[] ConvertResponseStreamToChatCompletions(byte[] body, string requestModel)
    {
        var input = Encoding.UTF8.GetString(body);
        var lines = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var output = new StringBuilder();
        var state = new ChatCompletionStreamState(requestModel);

        foreach (var line in lines)
        {
            var translated = TranslateResponseEventLine(line, state);
            if (!string.IsNullOrEmpty(translated))
            {
                output.Append(translated);
            }
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>
    /// Streams translated OpenAI chat completion chunks to the output stream.
    /// </summary>
    /// <param name="response">The upstream HTTP response.</param>
    /// <param name="output">The output stream to write to.</param>
    /// <param name="requestModel">The requested model identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is fully written.</returns>
    public static async Task WriteOpenAiStreamAsync(HttpResponseMessage response, Stream output, string requestModel, CancellationToken cancellationToken)
    {
        await using var upstreamStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var state = new ChatCompletionStreamState(requestModel);

        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var translated = TranslateResponseEventLine(line, state);
                if (string.IsNullOrEmpty(translated))
                {
                    continue;
                }

                await writer.WriteAsync(translated.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Passes through upstream SSE events to the output stream without translation.
    /// </summary>
    /// <param name="response">The upstream HTTP response.</param>
    /// <param name="output">The output stream to write to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the stream is fully written.</returns>
    public static async Task WriteResponsesStreamAsync(HttpResponseMessage response, Stream output, CancellationToken cancellationToken)
    {
        await using var upstreamStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);

        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Translates a single SSE event line from ChatGPT response format to OpenAI chat completion chunk format.
    /// </summary>
    internal static string TranslateResponseEventLine(string line, ChatCompletionStreamState state)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var payload = line[5..].Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return string.Empty;
        }

        var eventType = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        if (eventType == "response.created")
        {
            var response = root.TryGetProperty("response", out var responseElement) ? responseElement : root;
            if (response.TryGetProperty("id", out var idElement))
            {
                state.ResponseId = idElement.GetString() ?? state.ResponseId;
            }

            if (response.TryGetProperty("model", out var modelElement))
            {
                state.Model = modelElement.GetString() ?? state.Model;
            }

            if (response.TryGetProperty("created_at", out var createdElement) && createdElement.ValueKind == JsonValueKind.Number)
            {
                state.Created = createdElement.GetInt64();
            }

            AppendChunk(output, state, new Dictionary<string, object?>
            {
                ["role"] = "assistant"
            }, null);
            return output.ToString();
        }

        if (eventType == "response.output_item.added")
        {
            if (!TryGetFunctionCallItem(root))
            {
                return string.Empty;
            }

            var toolCall = UpsertToolCallFromItem(root, state.ToolCallsById);
            if (toolCall is null)
            {
                return string.Empty;
            }

            state.SawToolCall = true;
            AppendChunk(output, state, CreateToolCallDelta(toolCall, string.Empty), null);
            return output.ToString();
        }

        if (eventType == "response.output_text.delta")
        {
            if (root.TryGetProperty("delta", out var deltaElement))
            {
                var contentDelta = deltaElement.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    AppendChunk(output, state, new Dictionary<string, object?>
                    {
                        ["content"] = contentDelta
                    }, null);
                }
            }

            return output.ToString();
        }

        if (eventType == "response.reasoning_summary_text.delta")
        {
            if (!root.TryGetProperty("delta", out var reasoningDeltaElement))
            {
                return string.Empty;
            }

            var reasoningDelta = reasoningDeltaElement.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(reasoningDelta))
            {
                return string.Empty;
            }

            AppendChunk(output, state, new Dictionary<string, object?>
            {
                ["reasoning_content"] = reasoningDelta
            }, null);
            return output.ToString();
        }

        if (eventType is "response.reasoning_summary_text.done"
            or "response.reasoning_summary_part.added"
            or "response.reasoning_summary_part.done"
            or "response.reasoning_summary.done"
            or "response.content_part.added"
            or "response.output_text.done"
            or "response.content_part.done"
            or "response.function_call_arguments.done"
            or "response.function_call_output")
        {
            return string.Empty;
        }

        if (eventType == "response.output_item.done")
        {
            if (ChatGptResponseConverter.TryGetReasoningSummaryText(root, out var reasoningText))
            {
                AppendChunk(output, state, new Dictionary<string, object?>
                {
                    ["reasoning_content"] = reasoningText
                }, null);
            }

            return output.ToString();
        }

        if (eventType == "response.function_call_arguments.delta")
        {
            var toolCall = ResolveToolCallForArgumentsDelta(root, state.ToolCallsById);
            if (toolCall is null)
            {
                return string.Empty;
            }

            state.SawToolCall = true;
            var argumentDelta = root.TryGetProperty("delta", out var functionArgumentsDeltaElement)
                ? functionArgumentsDeltaElement.GetString() ?? string.Empty
                : string.Empty;
            AppendChunk(output, state, CreateToolCallDelta(toolCall, argumentDelta), null);
            return output.ToString();
        }

        if (eventType == "response.completed")
        {
            AppendChunk(output, state, new Dictionary<string, object?>(), state.SawToolCall ? "tool_calls" : "stop");
            output.Append("data: [DONE]\n\n");
        }

        return output.ToString();
    }

    /// <summary>
    /// Creates a tool call delta dictionary for a chat completion chunk.
    /// </summary>
    /// <param name="toolCall">The tool call state.</param>
    /// <param name="arguments">The arguments delta string.</param>
    /// <returns>A dictionary containing the tool call delta.</returns>
    private static Dictionary<string, object?> CreateToolCallDelta(ToolCallState toolCall, string arguments)
    {
        return new Dictionary<string, object?>
        {
            ["tool_calls"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = toolCall.Index,
                    ["id"] = toolCall.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = toolCall.Name,
                        ["arguments"] = arguments
                    }
                }
            }
        };
    }

    /// <summary>
    /// Resolves the tool call state associated with an arguments delta event.
    /// </summary>
    /// <param name="root">The event JSON element.</param>
    /// <param name="toolCallsById">The tool call state lookup table.</param>
    /// <returns>The resolved tool call state, or <see langword="null"/>.</returns>
    private static ToolCallState? ResolveToolCallForArgumentsDelta(JsonElement root, IDictionary<string, ToolCallState> toolCallsById)
    {
        if (TryGetToolCallIdentifier(root, out var toolCallId) && toolCallsById.TryGetValue(toolCallId, out var toolCall))
        {
            return toolCall;
        }

        return null;
    }

    /// <summary>
    /// Creates or updates a tool call state from a ChatGPT output item event.
    /// </summary>
    /// <param name="root">The event JSON element.</param>
    /// <param name="toolCallsById">The tool call state lookup table.</param>
    /// <returns>The created or retrieved tool call state, or <see langword="null"/>.</returns>
    private static ToolCallState? UpsertToolCallFromItem(JsonElement root, IDictionary<string, ToolCallState> toolCallsById)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var callId = itemElement.TryGetProperty("call_id", out var callIdElement)
            ? callIdElement.GetString()
            : null;
        var itemId = itemElement.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : null;
        var stableToolCallId = !string.IsNullOrWhiteSpace(callId) ? callId : itemId;
        if (string.IsNullOrWhiteSpace(stableToolCallId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(callId) && toolCallsById.TryGetValue(callId, out var existingToolCall))
        {
            return existingToolCall;
        }

        if (!string.IsNullOrWhiteSpace(itemId) && toolCallsById.TryGetValue(itemId, out existingToolCall))
        {
            return existingToolCall;
        }

        var toolCall = new ToolCallState(
            stableToolCallId,
            toolCallsById.Values
                .Select(existing => existing.Id)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            itemElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty);

        if (!string.IsNullOrWhiteSpace(callId))
        {
            toolCallsById[callId] = toolCall;
        }

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            toolCallsById[itemId] = toolCall;
        }

        return toolCall;
    }

    /// <summary>
    /// Attempts to extract a tool call identifier from an event payload using various known property names.
    /// </summary>
    /// <param name="root">The event JSON element.</param>
    /// <param name="toolCallId">The extracted tool call identifier.</param>
    /// <returns><see langword="true"/> if an identifier was found; otherwise <see langword="false"/>.</returns>
    private static bool TryGetToolCallIdentifier(JsonElement root, out string toolCallId)
    {
        foreach (var propertyName in new[] { "item_id", "call_id", "id" })
        {
            if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    toolCallId = value;
                    return true;
                }
            }
        }

        toolCallId = string.Empty;
        return false;
    }

    /// <summary>
    /// Determines whether an event item represents a function call.
    /// </summary>
    /// <param name="root">The event JSON element.</param>
    /// <returns><see langword="true"/> if it is a function call; otherwise <see langword="false"/>.</returns>
    private static bool TryGetFunctionCallItem(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return itemElement.TryGetProperty("type", out var typeElement)
            && string.Equals(typeElement.GetString(), "function_call", StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a chat completion chunk to the output string builder.
    /// </summary>
    /// <param name="output">The string builder to append to.</param>
    /// <param name="state">The current stream translation state.</param>
    /// <param name="delta">The message delta content.</param>
    /// <param name="finishReason">The optional finish reason.</param>
    private static void AppendChunk(StringBuilder output, ChatCompletionStreamState state, Dictionary<string, object?> delta, string? finishReason)
    {
        if (!state.EmittedRole && !delta.ContainsKey("role"))
        {
            delta = new Dictionary<string, object?>(delta)
            {
                ["role"] = "assistant"
            };
            state.EmittedRole = true;
        }
        else if (delta.ContainsKey("role"))
        {
            state.EmittedRole = true;
        }

        var chunk = new Dictionary<string, object?>
        {
            ["id"] = state.ResponseId,
            ["object"] = "chat.completion.chunk",
            ["created"] = state.Created,
            ["model"] = state.Model,
            ["choices"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason
                }
            }
        };

        output.Append("data: ");
        output.Append(JsonSerializer.Serialize(chunk, SerializerOptions));
        output.Append("\n\n");
    }

    /// <summary>
    /// Tracks state during stream translation for a single request.
    /// </summary>
    internal sealed class ChatCompletionStreamState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ChatCompletionStreamState"/> class.
        /// </summary>
        /// <param name="requestModel">The requested model identifier.</param>
        public ChatCompletionStreamState(string requestModel)
        {
            Model = requestModel;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the assistant role has been emitted in the stream.
        /// </summary>
        public bool EmittedRole { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a tool call has been encountered in the stream.
        /// </summary>
        public bool SawToolCall { get; set; }

        /// <summary>
        /// Gets or sets the unique response identifier.
        /// </summary>
        public string ResponseId { get; set; } = $"chatcmpl-{Guid.NewGuid():N}";

        /// <summary>
        /// Gets or sets the model identifier used for the response.
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Gets or sets the creation timestamp in Unix seconds.
        /// </summary>
        public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Gets a lookup table of active tool calls being tracked in the stream.
        /// </summary>
        public IDictionary<string, ToolCallState> ToolCallsById { get; } = new Dictionary<string, ToolCallState>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Tracks the state of a single tool call during stream translation.
    /// </summary>
    /// <param name="Id">The unique tool call identifier.</param>
    /// <param name="Index">The tool call index in the choices array.</param>
    /// <param name="Name">The name of the tool being called.</param>
    internal sealed record ToolCallState(string Id, int Index, string Name);
}
