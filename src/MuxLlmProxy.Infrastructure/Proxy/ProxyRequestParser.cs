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

        var anthropicRequest = JsonSerializer.Deserialize<AnthropicMessagesRequest>(body, SerializerOptions)
            ?? throw new InvalidOperationException("The request body is required.");

        if (string.IsNullOrWhiteSpace(anthropicRequest.Model))
        {
            throw new InvalidOperationException("The request model is required.");
        }

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
}
