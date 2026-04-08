using System.Net.Http.Headers;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Infrastructure.Providers;

namespace MuxLlmProxy.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// Implements an OpenAI-compatible provider adapter.
/// </summary>
public sealed class OpenAiCompatibleAdapter : IProviderAdapter
{
    private readonly IMessageTranslator _messageTranslator;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleAdapter"/> class.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="messageTranslator">The message translator dependency.</param>
    public OpenAiCompatibleAdapter(string providerId, IMessageTranslator messageTranslator)
    {
        ProviderId = providerId;
        _messageTranslator = messageTranslator;
    }

    /// <summary>
    /// Gets the provider identifier handled by this adapter.
    /// </summary>
    public string ProviderId { get; }

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

        var message = new HttpRequestMessage(HttpMethod.Post, $"{target.ProviderType.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new ByteArrayContent(_messageTranslator.ToOpenAiRequest(request.Body))
        };

        message.Content.Headers.ContentType = new MediaTypeHeaderValue(ProxyConstants.ContentTypes.Json);
        if (!string.IsNullOrWhiteSpace(target.Account.Token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(ProxyConstants.Responses.BearerScheme, target.Account.Token);
        }

        foreach (var header in ProviderHttpUtilities.ParseCustomHeaders(target.ProviderType.CustomHeaders))
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

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

        var isAnthropicRequest = request.Format == ProxyFormat.Anthropic;
        if (request.Stream && !isAnthropicRequest)
        {
            return new ProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.EventStreamUtf8),
                WriteBodyAsync = async (output, ct) =>
                {
                    await using var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
                    try
                    {
                        await upstreamStream.CopyToAsync(output, ct);
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }
            };
        }

        var bufferedBody = body ?? await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var translatedBody = isAnthropicRequest
            ? request.Stream
                ? _messageTranslator.ToAnthropicStream(bufferedBody, request.Model)
                : _messageTranslator.ToAnthropicResponse(bufferedBody, request.Model)
            : bufferedBody;

        return new ProxyResponse
        {
            StatusCode = (int)response.StatusCode,
            Headers = ProviderHttpUtilities.CreateJsonHeaders(request.Stream ? ProxyConstants.ContentTypes.EventStreamUtf8 : ProxyConstants.ContentTypes.Json),
            Body = translatedBody
        };
    }

    /// <summary>
    /// Returns an optional provider limit snapshot for an account.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The limit snapshot, or <see langword="null"/> when unavailable.</returns>
    public Task<ProviderLimitSnapshot?> GetLimitSnapshotAsync(ProxyTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult(ProviderHttpUtilities.CreateWeeklyLimitSnapshot(target.Account.AvailableAtWeeklyLimit));
    }
}
