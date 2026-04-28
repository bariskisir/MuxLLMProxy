using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Proxy;

/// <summary>
/// Parses inbound request bodies into normalized proxy requests.
/// </summary>
public sealed class ProxyRequestParser : IProxyRequestParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses the inbound request body.
    /// </summary>
    /// <param name="body">The raw request body bytes.</param>
    /// <param name="formatHint">The expected payload format inferred from the route.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized proxy request.</returns>
    public Task<ProxyRequest> ParseAsync(byte[] body, ProxyFormat? formatHint, CancellationToken cancellationToken)
    {
        var prefersAnthropic = formatHint == ProxyFormat.Anthropic;
        var prefersResponses = formatHint == ProxyFormat.OpenAiResponses;

        if (prefersResponses && TryParseOpenAiResponses(body, out var responsesModel, out var responsesStream))
        {
            return Task.FromResult(new ProxyRequest
            {
                Model = responsesModel,
                Stream = responsesStream,
                Format = ProxyFormat.OpenAiResponses,
                Body = body
            });
        }

        if (!prefersAnthropic && TryParseOpenAi(body, out var openAiRequest))
        {
            return Task.FromResult(new ProxyRequest
            {
                Model = openAiRequest.Model,
                Stream = openAiRequest.Stream,
                Format = ProxyFormat.OpenAi,
                Body = body
            });
        }

        var anthropicRequest = ParseAnthropic(body);

        return Task.FromResult(new ProxyRequest
        {
            Model = anthropicRequest.Model,
            Stream = anthropicRequest.Stream,
            Format = ProxyFormat.Anthropic,
            Body = body
        });
    }

    /// <summary>
    /// Attempts to parse the request as an OpenAI chat completions payload.
    /// </summary>
    /// <param name="body">The raw request body bytes.</param>
    /// <param name="request">The parsed OpenAI request.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise <see langword="false"/>.</returns>
    private static bool TryParseOpenAi(byte[] body, out OpenAiChatRequest request)
    {
        try
        {
            request = JsonSerializer.Deserialize<OpenAiChatRequest>(body, SerializerOptions)!;
            return request is not null && !string.IsNullOrWhiteSpace(request.Model) && request.Messages is not null;
        }
        catch (JsonException)
        {
            request = null!;
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse the request as a ChatGPT-specific 'responses' backend API payload.
    /// </summary>
    /// <param name="body">The raw request body bytes.</param>
    /// <param name="model">The extracted model name.</param>
    /// <param name="stream">The extracted stream preference.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise <see langword="false"/>.</returns>
    private static bool TryParseOpenAiResponses(byte[] body, out string model, out bool stream)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("model", out var modelElement) || modelElement.ValueKind != JsonValueKind.String)
            {
                model = string.Empty;
                stream = false;
                return false;
            }

            var hasInput = root.TryGetProperty("input", out _);
            var hasInstructions = root.TryGetProperty("instructions", out _);
            var hasReasoning = root.TryGetProperty("reasoning", out _);
            if (!hasInput && !hasInstructions && !hasReasoning)
            {
                model = string.Empty;
                stream = false;
                return false;
            }

            model = modelElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model))
            {
                stream = false;
                return false;
            }

            stream = root.TryGetProperty("stream", out var streamElement)
                && streamElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && streamElement.GetBoolean();
            return true;
        }
        catch (JsonException)
        {
            model = string.Empty;
            stream = false;
            return false;
        }
    }

    private static (string Model, bool Stream) ParseAnthropic(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The request body is required.");
        }

        if (!root.TryGetProperty("model", out var modelElement)
            || modelElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(modelElement.GetString()))
        {
            throw new InvalidOperationException("The request model is required.");
        }

        if (!root.TryGetProperty("messages", out var messagesElement)
            || messagesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The request messages are required.");
        }

        var stream = !root.TryGetProperty("stream", out var streamElement)
            || streamElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || streamElement.GetBoolean();

        return (modelElement.GetString()!, stream);
    }
}
