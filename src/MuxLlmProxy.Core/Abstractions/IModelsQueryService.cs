using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines model discovery operations.
/// </summary>
public interface IModelsQueryService
{
    /// <summary>
    /// Returns the distinct models exposed by enabled accounts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The distinct exposed models.</returns>
    Task<IReadOnlyList<ProviderModel>> GetModelsAsync(CancellationToken cancellationToken);
}
