using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines parsing of inbound body payloads into normalized proxy requests.
/// </summary>
public interface IProxyRequestParser
{
    /// <summary>
    /// Parses the inbound request body.
    /// </summary>
    /// <param name="body">The raw request body bytes.</param>
    /// <param name="formatHint">The expected payload format inferred from the route.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized proxy request.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the request is invalid.</exception>
    Task<ProxyRequest> ParseAsync(byte[] body, ProxyFormat? formatHint, CancellationToken cancellationToken);
}
