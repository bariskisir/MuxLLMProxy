namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a concrete provider account used to route upstream requests.
/// </summary>
public sealed record Account
{
    /// <summary>
    /// Gets the account identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the provider type identifier.
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the account is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the static token used by token-based providers.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Gets the ChatGPT access token.
    /// </summary>
    public string? Access { get; init; }

    /// <summary>
    /// Gets the ChatGPT refresh token.
    /// </summary>
    public string? Refresh { get; init; }

    /// <summary>
    /// Gets the ChatGPT expiration timestamp in Unix milliseconds.
    /// </summary>
    public long? Expire { get; init; }

    /// <summary>
    /// Gets the temporary rate-limit cooldown timestamp in Unix seconds.
    /// </summary>
    public long? AvailableAtRateLimit { get; init; }

    /// <summary>
    /// Gets the weekly limit cooldown timestamp in Unix seconds.
    /// </summary>
    public long? AvailableAtWeeklyLimit { get; init; }
}
