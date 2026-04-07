using MuxLlmProxy.Core.Contracts;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines account limit query operations.
/// </summary>
public interface ILimitsQueryService
{
    /// <summary>
    /// Returns limit entries for all configured accounts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The limit entries.</returns>
    Task<IReadOnlyList<LimitEntry>> GetLimitsAsync(CancellationToken cancellationToken);
}
