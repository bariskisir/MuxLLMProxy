using MuxLlmProxy.Core.Configuration;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines access to the runtime proxy configuration.
/// </summary>
public interface IProxyConfigurationProvider
{
    /// <summary>
    /// Loads the runtime proxy configuration.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The loaded proxy configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configuration file is invalid.</exception>
    Task<ProxyOptions> GetAsync(CancellationToken cancellationToken);
}
