using Microsoft.Extensions.Options;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;

namespace MuxLlmProxy.Infrastructure.Persistence;

/// <summary>
/// Loads runtime proxy configuration from bound application settings.
/// </summary>
public sealed class ProxyConfigurationProvider : IProxyConfigurationProvider
{
    private readonly ProxyOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyConfigurationProvider"/> class.
    /// </summary>
    /// <param name="options">The bound proxy options.</param>
    /// <exception cref="InvalidOperationException">Thrown when the configured port is invalid.</exception>
    public ProxyConfigurationProvider(IOptions<ProxyOptions> options)
    {
        _options = options.Value;

        if (_options.Port <= 0)
        {
            throw new InvalidOperationException("The configured port must be greater than zero.");
        }
    }

    /// <summary>
    /// Loads the runtime proxy configuration.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The loaded configuration.</returns>
    public Task<ProxyOptions> GetAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_options with
        {
            Token = _options.Token ?? string.Empty,
            Timeout = _options.GetNormalizedTimeoutSeconds()
        });
    }
}
