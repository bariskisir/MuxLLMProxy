using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines provider target resolution for a requested model.
/// </summary>
public interface ITargetSelector
{
    /// <summary>
    /// Resolves eligible targets for a requested model.
    /// </summary>
    /// <param name="requestedModel">The requested model identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ordered list of eligible targets.</returns>
    Task<IReadOnlyList<ProxyTarget>> SelectAsync(string requestedModel, CancellationToken cancellationToken);
}
