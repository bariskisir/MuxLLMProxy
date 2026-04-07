using System.Net.Http;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines provider-specific request preparation and response translation.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>
    /// Gets the provider identifier handled by this adapter.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Prepares an upstream HTTP request for the selected target.
    /// </summary>
    /// <param name="target">The selected target.</param>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upstream request message.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the request cannot be translated for the provider.</exception>
    Task<HttpRequestMessage> PrepareRequestAsync(ProxyTarget target, ProxyRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Converts an upstream response into the proxy response payload.
    /// </summary>
    /// <param name="target">The selected target.</param>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="response">The upstream response message.</param>
    /// <param name="body">The fully buffered upstream response body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The translated upstream response.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the upstream payload cannot be translated.</exception>
    Task<ProxyResponse> TranslateResponseAsync(ProxyTarget target, ProxyRequest request, HttpResponseMessage response, byte[] body, CancellationToken cancellationToken);

    /// <summary>
    /// Returns an optional provider limit snapshot for an account.
    /// </summary>
    /// <param name="target">The selected target.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The limit snapshot, or <see langword="null"/> when unavailable.</returns>
    Task<ProviderLimitSnapshot?> GetLimitSnapshotAsync(ProxyTarget target, CancellationToken cancellationToken);
}
