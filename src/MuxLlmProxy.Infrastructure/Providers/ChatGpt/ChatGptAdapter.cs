using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;
using MuxLlmProxy.Infrastructure.Providers;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Implements the ChatGPT provider adapter.
/// </summary>
public sealed partial class ChatGptAdapter : IProviderAdapter
{
    private readonly IMessageTranslator _messageTranslator;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGptAdapter"/> class.
    /// </summary>
    /// <param name="messageTranslator">The message translator dependency.</param>
    /// <param name="httpClientFactory">The HTTP client factory dependency.</param>
    public ChatGptAdapter(IMessageTranslator messageTranslator, IHttpClientFactory httpClientFactory)
    {
        _messageTranslator = messageTranslator;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the provider identifier handled by this adapter.
    /// </summary>
    public string ProviderId => ProxyConstants.Providers.ChatGpt;

    /// <summary>
    /// Prepares an upstream HTTP request for the selected target.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upstream HTTP request.</returns>
    public Task<HttpRequestMessage> PrepareRequestAsync(ProxyTarget target, ProxyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        var message = new HttpRequestMessage(HttpMethod.Post, $"{target.ProviderType.BaseUrl.TrimEnd('/')}/backend-api/codex/responses")
        {
            Content = new ByteArrayContent(BuildChatGptRequestBody(request))
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue(ProxyConstants.ContentTypes.Json);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Stream ? ProxyConstants.ContentTypes.EventStream : ProxyConstants.ContentTypes.Json));

        var accountId = ExtractChatGptAccountId(target.Account.Access);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.ChatGptAccountId, accountId);
        }

        if (string.IsNullOrWhiteSpace(target.Account.Access))
        {
            throw new InvalidOperationException($"ChatGPT account '{target.Account.Id}' is missing its access token.");
        }

        message.Headers.Authorization = new AuthenticationHeaderValue(ProxyConstants.Responses.BearerScheme, target.Account.Access);
        return Task.FromResult(message);
    }

    /// <summary>
    /// Converts an upstream response into the proxy response payload.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="response">The upstream HTTP response.</param>
    /// <param name="body">The buffered upstream response body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The translated proxy response.</returns>
    public async Task<ProxyResponse> TranslateResponseAsync(ProxyTarget target, ProxyRequest request, HttpResponseMessage response, byte[]? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccessStatusCode)
        {
            var contentType = response.Content.Headers.ContentType?.ToString() ?? ProxyConstants.ContentTypes.Json;
            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(contentType),
                Body = body ?? await response.Content.ReadAsByteArrayAsync(cancellationToken)
            };
        }

        if (request.Stream && request.Format == ProxyFormat.OpenAi)
        {
            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.EventStreamUtf8),
                WriteBodyAsync = (output, ct) => WriteOpenAiStreamAsync(response, output, request.Model, ct)
            };
        }

        var bufferedBody = body ?? await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var chatCompletionsStream = ConvertResponseStreamToChatCompletions(bufferedBody, request.Model);

        if (request.Format == ProxyFormat.OpenAi)
        {
            var translatedBody = request.Stream
                ? chatCompletionsStream
                : ConvertResponseToChatCompletion(ExtractCompletedResponse(bufferedBody), request.Model);

            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(request.Stream ? ProxyConstants.ContentTypes.EventStreamUtf8 : ProxyConstants.ContentTypes.Json),
                Body = translatedBody
            };
        }

        var anthropicBody = request.Stream
            ? _messageTranslator.ToAnthropicStream(chatCompletionsStream, request.Model)
            : _messageTranslator.ToAnthropicResponse(ConvertResponseToChatCompletion(ExtractCompletedResponse(bufferedBody), request.Model), request.Model);

        return new ProxyResponse
        {
            StatusCode = (int)response.StatusCode,
            Headers = ProviderHttpUtilities.CreateJsonHeaders(request.Stream ? ProxyConstants.ContentTypes.EventStreamUtf8 : ProxyConstants.ContentTypes.Json),
            Body = anthropicBody
        };
    }

    /// <summary>
    /// Returns an optional provider limit snapshot for an account.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The limit snapshot, or <see langword="null"/> when unavailable.</returns>
    public async Task<ProviderLimitSnapshot?> GetLimitSnapshotAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.Account.Access))
        {
            return ProviderHttpUtilities.CreateWeeklyLimitSnapshot(target.Account.AvailableAtWeeklyLimit);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.ProviderType.BaseUrl.TrimEnd('/')}/backend-api/wham/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ProxyConstants.ContentTypes.Json));
        request.Headers.TryAddWithoutValidation(ProxyConstants.Headers.Originator, ProxyConstants.ProviderHeaders.CodexOriginator);
        request.Headers.Authorization = new AuthenticationHeaderValue(ProxyConstants.Responses.BearerScheme, target.Account.Access);

        var accountId = ExtractChatGptAccountId(target.Account.Access);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation(ProxyConstants.Headers.ChatGptAccountId, accountId);
        }

        using var response = await _httpClientFactory.CreateClient("upstream").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to read ChatGPT limits: {(int)response.StatusCode}.");
        }

        return ParseBestLimitSnapshot(body)
            ?? ProviderHttpUtilities.CreateWeeklyLimitSnapshot(target.Account.AvailableAtWeeklyLimit);
    }

    /// <summary>
    /// Builds the ChatGPT backend-api request payload.
    /// </summary>
    /// <param name="request">The normalized proxy request.</param>
    /// <returns>The backend-api request payload.</returns>
    private byte[] BuildChatGptRequestBody(ProxyRequest request)
    {
        var openAiRequest = JsonSerializer.Deserialize<OpenAiChatRequest>(_messageTranslator.ToOpenAiRequest(request.Body), SerializerOptions)
            ?? throw new InvalidOperationException("The request payload could not be translated to an OpenAI-compatible format.");

        var instructions = string.Join(
            "\n\n",
            openAiRequest.Messages
                .Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase))
                .Select(message => FlattenMessageContent(message.Content))
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        var input = openAiRequest.Messages
            .Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase))
            .SelectMany(MapMessageToChatGptInput)
            .ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = openAiRequest.Model,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false
        };

        payload["instructions"] = string.IsNullOrWhiteSpace(instructions)
            ? ProxyConstants.Responses.DefaultInstructions
            : instructions;

        if (openAiRequest.TopP is not null)
        {
            payload["top_p"] = openAiRequest.TopP;
        }

        if (openAiRequest.Tools is not null)
        {
            payload["tools"] = openAiRequest.Tools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = tool.Type,
                ["name"] = tool.Function.Name,
                ["description"] = tool.Function.Description,
                ["parameters"] = tool.Function.Parameters
            }).ToArray();
            payload["parallel_tool_calls"] = false;
        }

        if (openAiRequest.ToolChoice is not null)
        {
            payload["tool_choice"] = NormalizeToolChoice(openAiRequest.ToolChoice);
        }

        if (!string.IsNullOrWhiteSpace(openAiRequest.Verbosity))
        {
            payload["text"] = new Dictionary<string, object?>
            {
                ["verbosity"] = openAiRequest.Verbosity
            };
        }

        if (!string.IsNullOrWhiteSpace(openAiRequest.ReasoningSummary))
        {
            payload["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = openAiRequest.ReasoningEffort,
                ["summary"] = openAiRequest.ReasoningSummary
            };
        }
        else if (!string.IsNullOrWhiteSpace(openAiRequest.ReasoningEffort))
        {
            payload["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = openAiRequest.ReasoningEffort
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    private static byte[] ConvertResponseToChatCompletion(byte[] body, string requestModel)
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

    private static byte[] ConvertResponseStreamToChatCompletions(byte[] body, string requestModel)
    {
        var input = Encoding.UTF8.GetString(body);
        var lines = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var output = new StringBuilder();
        var state = new ChatCompletionStreamState(requestModel);

        foreach (var line in lines)
        {
            AppendTranslatedEventLine(output, line, state);
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static async Task WriteOpenAiStreamAsync(HttpResponseMessage response, Stream output, string requestModel, CancellationToken cancellationToken)
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

    private static byte[] ExtractCompletedResponse(byte[] streamBody)
    {
        var lines = Encoding.UTF8.GetString(streamBody).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
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

            return Encoding.UTF8.GetBytes(responseElement.GetRawText());
        }

        throw new InvalidOperationException("The ChatGPT streaming response did not include a completed response payload.");
    }

    private sealed record ToolCallState(string Id, int Index, string Name);

    private sealed class ChatCompletionStreamState
    {
        public ChatCompletionStreamState(string requestModel)
        {
            Model = requestModel;
        }

        public bool EmittedRole { get; set; }

        public bool SawToolCall { get; set; }

        public string ResponseId { get; set; } = $"chatcmpl-{Guid.NewGuid():N}";

        public string Model { get; set; }

        public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public IDictionary<string, ToolCallState> ToolCallsById { get; } = new Dictionary<string, ToolCallState>(StringComparer.Ordinal);
    }

    private static void AppendTranslatedEventLine(StringBuilder output, string line, ChatCompletionStreamState state)
    {
        var translated = TranslateResponseEventLine(line, state);
        if (!string.IsNullOrEmpty(translated))
        {
            output.Append(translated);
        }
    }

    private static string TranslateResponseEventLine(string line, ChatCompletionStreamState state)
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

        if (eventType == "response.reasoning_summary_text.done"
            || eventType == "response.reasoning_summary_part.added"
            || eventType == "response.reasoning_summary_part.done"
            || eventType == "response.reasoning_summary.done"
            || eventType == "response.content_part.added"
            || eventType == "response.output_text.done"
            || eventType == "response.content_part.done"
            || eventType == "response.function_call_arguments.done"
            || eventType == "response.function_call_output")
        {
            return string.Empty;
        }

        if (eventType == "response.output_item.done")
        {
            if (TryGetReasoningSummaryText(root, out var reasoningText))
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

    private static ToolCallState? ResolveToolCallForArgumentsDelta(JsonElement root, IDictionary<string, ToolCallState> toolCallsById)
    {
        if (TryGetToolCallIdentifier(root, out var toolCallId) && toolCallsById.TryGetValue(toolCallId, out var toolCall))
        {
            return toolCall;
        }

        return null;
    }

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

    private static bool TryGetFunctionCallItem(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return itemElement.TryGetProperty("type", out var typeElement)
            && string.Equals(typeElement.GetString(), "function_call", StringComparison.Ordinal);
    }

    private static IEnumerable<Dictionary<string, object?>> MapMessageToChatGptInput(OpenAiMessage message)
    {
        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && TryGetToolCalls(message, out var toolCalls))
        {
            foreach (var toolCall in toolCalls)
            {
                yield return toolCall;
            }

            yield break;
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            var toolCallId = message.ToolCallId ?? ExtractToolCallId(message.Content);
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                throw new InvalidOperationException("Tool result messages must include a tool_call_id.");
            }

            yield return new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = toolCallId,
                ["output"] = FlattenMessageContent(message.Content)
            };
            yield break;
        }

        yield return new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text",
                    ["text"] = FlattenMessageContent(message.Content)
                }
            }
        };
    }

    private static bool TryGetToolCalls(OpenAiMessage message, out IReadOnlyList<Dictionary<string, object?>> toolCalls)
    {
        toolCalls = Array.Empty<Dictionary<string, object?>>();
        JsonElement toolCallsElement = default;

        if (message.ToolCalls is not null)
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(message.ToolCalls, SerializerOptions);
            using var document = JsonDocument.Parse(serialized);
            toolCallsElement = document.RootElement.Clone();
        }
        else if (message.Content is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("tool_calls", out var nestedToolCallsElement)
            && nestedToolCallsElement.ValueKind == JsonValueKind.Array)
        {
            toolCallsElement = nestedToolCallsElement;
        }
        else
        {
            return false;
        }

        if (toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        toolCalls = toolCallsElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var function = item.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object
                    ? functionElement
                    : default;

                return new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null,
                    ["name"] = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                    ["arguments"] = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.GetString() : null
                };
            })
            .Where(item => item["call_id"] is string callId && !string.IsNullOrWhiteSpace(callId))
            .ToArray();

        return toolCalls.Count > 0;
    }

    private static string? ExtractToolCallId(object? content)
    {
        if (content is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("tool_call_id", out var toolCallIdElement))
                {
                    return toolCallIdElement.GetString();
                }

                if (element.TryGetProperty("id", out var idElement))
                {
                    return idElement.GetString();
                }
            }

            return null;
        }

        if (content is IDictionary<string, object?> dictionary)
        {
            if (dictionary.TryGetValue("tool_call_id", out var toolCallIdValue) && toolCallIdValue is string toolCallId)
            {
                return toolCallId;
            }

            if (dictionary.TryGetValue("id", out var idValue) && idValue is string id)
            {
                return id;
            }

            if (dictionary.TryGetValue("content", out var nestedContent))
            {
                return ExtractToolCallId(nestedContent);
            }
        }

        return null;
    }

    private static object NormalizeToolChoice(object toolChoice)
    {
        if (toolChoice is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return toolChoice;
        }

        if (element.TryGetProperty("name", out var anthropicNameElement))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = anthropicNameElement.GetString()
            };
        }

        if (!element.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
        {
            return toolChoice;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "function" : "function",
            ["name"] = functionElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null
        };
    }

    private static Dictionary<string, object?> BuildAssistantMessage(string content, IReadOnlyList<Dictionary<string, object?>> toolCalls, string reasoningContent)
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

    private static List<Dictionary<string, object?>> ExtractToolCalls(JsonElement response)
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

    private static string ExtractOutputText(JsonElement response)
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

    private static string ExtractReasoningText(JsonElement response)
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

    private static bool TryGetReasoningSummaryText(JsonElement element, out string reasoningText)
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


    private static void AppendChunk(StringBuilder output, ChatCompletionStreamState state, Dictionary<string, object?> delta, string? finishReason)
    {
        var emittedRole = state.EmittedRole;
        AppendChunk(output, state.ResponseId, state.Created, state.Model, delta, finishReason, ref emittedRole);
        state.EmittedRole = emittedRole;
    }

    private static void AppendChunk(StringBuilder output, string id, long created, string model, Dictionary<string, object?> delta, string? finishReason, ref bool emittedRole)
    {
        if (!emittedRole && !delta.ContainsKey("role"))
        {
            delta = new Dictionary<string, object?>(delta)
            {
                ["role"] = "assistant"
            };
            emittedRole = true;
        }
        else if (delta.ContainsKey("role"))
        {
            emittedRole = true;
        }

        var chunk = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
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

    private static string FlattenMessageContent(object? content)
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
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textElement))
                {
                    parts.Add(textElement.GetString() ?? string.Empty);
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

    private static ProviderLimitSnapshot? ParseBestLimitSnapshot(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var snapshots = new List<ProviderLimitSnapshot>();

        if (document.RootElement.TryGetProperty("rate_limit", out var rateLimitElement))
        {
            TryAddSnapshot(snapshots, "Limit", rateLimitElement);
        }

        if (document.RootElement.TryGetProperty("code_review_rate_limit", out var codeReviewRateLimitElement))
        {
            TryAddSnapshot(snapshots, "Code Review", codeReviewRateLimitElement);
        }

        if (document.RootElement.TryGetProperty("additional_rate_limits", out var additionalRateLimitsElement)
            && additionalRateLimitsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var additionalLimit in additionalRateLimitsElement.EnumerateArray())
            {
                var label = additionalLimit.TryGetProperty("limit_name", out var limitNameElement)
                    ? limitNameElement.GetString() ?? "Limit"
                    : "Limit";
                if (additionalLimit.TryGetProperty("rate_limit", out var additionalRateLimit))
                {
                    TryAddSnapshot(snapshots, label, additionalRateLimit);
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.WindowDurationMins == ProxyConstants.Defaults.WeeklyLimitWindowMinutes ? 0 : 1)
            .ThenBy(snapshot => snapshot.WindowDurationMins)
            .FirstOrDefault();
    }

    private static void TryAddSnapshot(ICollection<ProviderLimitSnapshot> snapshots, string label, JsonElement rateLimitElement)
    {
        if (rateLimitElement.ValueKind != JsonValueKind.Object
            || !rateLimitElement.TryGetProperty("primary_window", out var primaryWindow)
            || primaryWindow.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var windowDurationMins = TryGetWindowDurationMins(primaryWindow);
        var usedPercent = TryGetInt32(primaryWindow, "used_percent");
        var resetsAt = TryGetInt64(primaryWindow, "reset_at");
        if (windowDurationMins is null || usedPercent is null || resetsAt is null)
        {
            return;
        }

        snapshots.Add(new ProviderLimitSnapshot(
            windowDurationMins == ProxyConstants.Defaults.WeeklyLimitWindowMinutes ? ProxyConstants.Labels.WeeklyLimit : label,
            Math.Max(0, ProxyConstants.Defaults.WeeklyLimitPercent - usedPercent.Value),
            usedPercent.Value,
            resetsAt.Value,
            windowDurationMins.Value));
    }

    private static int? TryGetWindowDurationMins(JsonElement window)
    {
        var rawSeconds = TryGetInt64(window, "limit_window_seconds");
        if (rawSeconds is null || rawSeconds <= 0)
        {
            return null;
        }

        return (int)Math.Ceiling(rawSeconds.Value / 60d);
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ExtractChatGptAccountId(string? accessToken)
    {
        return JwtUtilities.TryReadStringClaim(accessToken, "https://api.openai.com/auth", "chatgpt_account_id");
    }
}
