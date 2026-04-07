using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Coordinates request routing, upstream invocation, and failover.
/// </summary>
public interface IProxyOrchestrator
{
    /// <summary>
    /// Executes the proxy workflow for a normalized request.
    /// </summary>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The translated proxy response.</returns>
    /// <exception cref="InvalidOperationException">Thrown when routing cannot be completed successfully.</exception>
    Task<ProxyResponse> ExecuteAsync(ProxyRequest request, CancellationToken cancellationToken);
}
