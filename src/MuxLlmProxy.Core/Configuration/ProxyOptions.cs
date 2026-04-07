namespace MuxLlmProxy.Core.Configuration;

/// <summary>
/// Represents the runtime configuration for the proxy host.
/// </summary>
public sealed record ProxyOptions
{
    /// <summary>
    /// Gets the listening port used by the HTTP host.
    /// </summary>
    public int Port { get; init; } = ProxyConstants.Defaults.Port;

    /// <summary>
    /// Gets the optional bearer token required by protected endpoints.
    /// </summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// Gets the request timeout in seconds used for both inbound handling and upstream calls.
    /// </summary>
    public int Timeout { get; init; } = ProxyConstants.Defaults.TimeoutSeconds;

    /// <summary>
    /// Returns the effective request timeout in seconds.
    /// </summary>
    public int GetNormalizedTimeoutSeconds()
    {
        return Timeout > 0 ? Timeout : ProxyConstants.Defaults.TimeoutSeconds;
    }
}
