using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines access to the provider type catalog.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>
    /// Loads the provider type catalog.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The configured provider types.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the type catalog is invalid.</exception>
    Task<IReadOnlyList<ProviderType>> GetAsync(CancellationToken cancellationToken);
}
