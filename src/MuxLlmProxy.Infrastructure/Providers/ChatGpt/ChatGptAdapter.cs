using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;
using MuxLlmProxy.Infrastructure.Translation;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Implements the ChatGPT provider adapter.
/// </summary>
public sealed class ChatGptAdapter : IProviderAdapter
{
    private readonly IMessageTranslator _messageTranslator;
    private readonly ChatGptAuthService _authService;
    private readonly ChatGptRequestTransformer _requestTransformer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatGptAdapter> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGptAdapter"/> class.
    /// </summary>
    /// <param name="messageTranslator">The message translator dependency.</param>
    /// <param name="authService">The ChatGPT authentication service.</param>
    /// <param name="requestTransformer">The ChatGPT request transformer.</param>
    /// <param name="httpClientFactory">The HTTP client factory dependency.</param>
    /// <param name="logger">The logger dependency.</param>
    public ChatGptAdapter(
        IMessageTranslator messageTranslator,
        ChatGptAuthService authService,
        ChatGptRequestTransformer requestTransformer,
        IHttpClientFactory httpClientFactory,
        ILogger<ChatGptAdapter> logger)
    {
        _messageTranslator = messageTranslator;
        _authService = authService;
        _requestTransformer = requestTransformer;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
    public async Task<HttpRequestMessage> PrepareRequestAsync(ProxyTarget target, ProxyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        var payload = await _requestTransformer.TransformAsync(request, cancellationToken);
        LogToolRequestPayload(request, payload);
        var message = new HttpRequestMessage(HttpMethod.Post, $"{target.ProviderType.BaseUrl.TrimEnd('/')}/backend-api/codex/responses")
        {
            Content = new ByteArrayContent(payload)
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue(ProxyConstants.ContentTypes.Json);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ProxyConstants.ContentTypes.EventStream));
        message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.OpenAiBeta, ProxyConstants.ProviderHeaders.OpenAiBetaResponses);
        message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.Originator, ProxyConstants.ProviderHeaders.CodexOriginator);

        var accessContext = await _authService.GetAccessContextAsync(target.Account, cancellationToken);
        var accountId = accessContext.AccountId;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.ChatGptAccountId, accountId);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.SessionId, request.SessionId);
            message.Headers.TryAddWithoutValidation(ProxyConstants.Headers.ConversationId, request.SessionId);
        }

        message.Headers.Authorization = new AuthenticationHeaderValue(ProxyConstants.Responses.BearerScheme, accessContext.AccessToken);
        return message;
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
            var mappedResponse = await MapRateLimitResponseAsync(response, cancellationToken);
            var contentType = mappedResponse.Content.Headers.ContentType?.ToString() ?? ProxyConstants.ContentTypes.Json;
            return new ProxyResponse
            {
                StatusCode = (int)mappedResponse.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(contentType),
                Body = body ?? await mappedResponse.Content.ReadAsByteArrayAsync(cancellationToken)
            };
        }

        if (request.Stream && request.Format == ProxyFormat.OpenAi)
        {
            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.EventStreamUtf8),
                WriteBodyAsync = (output, ct) => ChatGptStreamTranslator.WriteOpenAiStreamAsync(response, output, request.Model, ct)
            };
        }

        if (request.Format == ProxyFormat.OpenAiResponses)
        {
            if (request.Stream)
            {
                return new ProxyResponse
                {
                    StatusCode = (int)response.StatusCode,
                    Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.EventStreamUtf8),
                    WriteBodyAsync = (output, ct) => ChatGptStreamTranslator.WriteResponsesStreamAsync(response, output, ct)
                };
            }

            var responsesBody = ChatGptResponseConverter.ExtractCompletedResponse(body ?? await response.Content.ReadAsByteArrayAsync(cancellationToken));
            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.Json),
                Body = responsesBody
            };
        }

        var bufferedBody = body ?? await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var chatCompletionsStream = ChatGptStreamTranslator.ConvertResponseStreamToChatCompletions(bufferedBody, request.Model);

        if (request.Format == ProxyFormat.OpenAi)
        {
            var translatedBody = request.Stream
                ? chatCompletionsStream
                : ChatGptResponseConverter.ConvertResponseToChatCompletion(ChatGptResponseConverter.ExtractCompletedResponse(bufferedBody), request.Model);

            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(request.Stream ? ProxyConstants.ContentTypes.EventStreamUtf8 : ProxyConstants.ContentTypes.Json),
                Body = translatedBody
            };
        }

        var anthropicBody = request.Stream
            ? _messageTranslator.ToAnthropicStream(chatCompletionsStream, request.Model)
            : _messageTranslator.ToAnthropicResponse(ChatGptResponseConverter.ConvertResponseToChatCompletion(ChatGptResponseConverter.ExtractCompletedResponse(bufferedBody), request.Model), request.Model);

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

        var accessContext = await _authService.GetAccessContextAsync(target.Account, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.ProviderType.BaseUrl.TrimEnd('/')}/backend-api/wham/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ProxyConstants.ContentTypes.Json));
        request.Headers.TryAddWithoutValidation(ProxyConstants.Headers.Originator, ProxyConstants.ProviderHeaders.CodexOriginator);
        request.Headers.Authorization = new AuthenticationHeaderValue(ProxyConstants.Responses.BearerScheme, accessContext.AccessToken);

        var accountId = accessContext.AccountId;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation(ProxyConstants.Headers.ChatGptAccountId, accountId);
        }

        using var response = await _httpClientFactory.CreateClient("upstream").SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to read ChatGPT limits: {(int)response.StatusCode}.");
        }

        return ChatGptLimitParser.ParseBestLimitSnapshot(responseBody)
            ?? ProviderHttpUtilities.CreateWeeklyLimitSnapshot(target.Account.AvailableAtWeeklyLimit);
    }

    /// <summary>
    /// Maps OpenAI chat messages to ChatGPT backend-api input items.
    /// </summary>
    /// <param name="message">The OpenAI message to map.</param>
    /// <returns>One or more backend-api input items.</returns>
    internal static IEnumerable<Dictionary<string, object?>> MapMessageToChatGptInput(OpenAiMessage message)
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
                yield return new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = OpenAiRequestNormalizer.FlattenMessageContent(message.Content)
                        }
                    }
                };
                yield break;
            }

            yield return new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = toolCallId,
                ["output"] = OpenAiRequestNormalizer.FlattenMessageContent(message.Content)
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
                    ["text"] = OpenAiRequestNormalizer.FlattenMessageContent(message.Content)
                }
            }
        };
    }

    /// <summary>
    /// Normalizes an OpenAI tool_choice value to the ChatGPT backend-api format.
    /// </summary>
    /// <param name="toolChoice">The original tool_choice value.</param>
    /// <returns>The normalized tool_choice value.</returns>
    internal static object NormalizeToolChoice(object toolChoice)
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

    /// <summary>
    /// Attempts to extract tool calls from an OpenAI message.
    /// </summary>
    /// <param name="message">The message to inspect.</param>
    /// <param name="toolCalls">The extracted tool calls.</param>
    /// <returns><see langword="true"/> when tool calls were found; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Recursively extracts a tool call identifier from content objects.
    /// </summary>
    /// <param name="content">The content object to inspect.</param>
    /// <returns>The tool call identifier, or <see langword="null"/>.</returns>
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

    /// <summary>
    /// Maps upstream error responses to proxy 429 responses when applicable.
    /// </summary>
    /// <param name="response">The original upstream response.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The potentially mapped response message.</returns>
    private static async Task<HttpResponseMessage> MapRateLimitResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            return response;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var bodyText = Encoding.UTF8.GetString(body);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return response;
        }

        string code = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.TryGetProperty("code", out var codeElement))
                {
                    code = codeElement.GetString() ?? string.Empty;
                }
                else if (errorElement.TryGetProperty("type", out var typeElement))
                {
                    code = typeElement.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        var haystack = $"{code} {bodyText}";
        if (haystack.IndexOf("usage_limit_reached", StringComparison.OrdinalIgnoreCase) < 0
            && haystack.IndexOf("usage_not_included", StringComparison.OrdinalIgnoreCase) < 0
            && haystack.IndexOf("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) < 0
            && haystack.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return response;
        }

        var mapped = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            ReasonPhrase = "Too Many Requests",
            Content = new ByteArrayContent(body)
        };

        foreach (var header in response.Headers)
        {
            mapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            mapped.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Dispose();
        return mapped;
    }

    /// <summary>
    /// Logs tool-related usage metadata from the request payload.
    /// </summary>
    /// <param name="request">The proxy request.</param>
    /// <param name="payload">The request payload bytes.</param>
    private void LogToolRequestPayload(ProxyRequest request, byte[] payload)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var hasTools = root.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array && toolsElement.GetArrayLength() > 0;
            var hasToolOutputs = PayloadContainsToolOutputs(root);
            if (!hasTools && !hasToolOutputs)
            {
                return;
            }

            _logger.LogInformation(
                "Tool request payload {@ToolRequest}",
                new
                {
                    request.SessionId,
                    Format = request.Format.ToString(),
                    request.Model,
                    ToolChoice = root.TryGetProperty("tool_choice", out var toolChoiceElement) ? toolChoiceElement.GetRawText() : null,
                    Tools = hasTools ? toolsElement.GetRawText() : null,
                    Input = root.TryGetProperty("input", out var inputElement) ? inputElement.GetRawText() : null
                });
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Failed to inspect tool request payload for session {SessionId}", request.SessionId);
        }
    }

    /// <summary>
    /// Determines whether a JSON payload contains tool output markers.
    /// </summary>
    /// <param name="root">The root JSON element.</param>
    /// <returns><see langword="true"/> when tool outputs are present; otherwise <see langword="false"/>.</returns>
    private static bool PayloadContainsToolOutputs(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var inputElement) || inputElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in inputElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            if (type is "function_call" or "function_call_output" or "local_shell_call" or "local_shell_call_output" or "custom_tool_call" or "custom_tool_call_output")
            {
                return true;
            }
        }

        return false;
    }
}
