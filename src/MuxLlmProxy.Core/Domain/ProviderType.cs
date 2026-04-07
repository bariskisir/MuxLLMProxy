namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a provider definition loaded from the type catalog.
/// </summary>
public sealed record ProviderType
{
    /// <summary>
    /// Gets the unique provider identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the provider base URL.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Gets optional provider-specific static headers.
    /// </summary>
    public string? CustomHeaders { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider exposes limit information.
    /// </summary>
    public bool HasLimits { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider supports multiple accounts per model.
    /// </summary>
    public bool SupportsMulti { get; init; }

    /// <summary>
    /// Gets the models exposed by the provider.
    /// </summary>
    public required IReadOnlyList<ProviderModel> Models { get; init; }

    /// <summary>
    /// Gets a value indicating whether proxy-managed availability windows apply to this provider.
    /// </summary>
    public bool TracksAvailabilityWindows => HasLimits;

    /// <summary>
    /// Determines whether the specified account is currently available for selection.
    /// </summary>
    /// <param name="account">The account to inspect.</param>
    /// <param name="currentUnixSeconds">The current Unix timestamp in seconds.</param>
    /// <returns><see langword="true"/> when the account is available; otherwise <see langword="false"/>.</returns>
    public bool IsAccountAvailable(Account account, long currentUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!TracksAvailabilityWindows)
        {
            return true;
        }

        return (account.AvailableAtRateLimit is null || account.AvailableAtRateLimit <= currentUnixSeconds)
            && (account.AvailableAtWeeklyLimit is null || account.AvailableAtWeeklyLimit <= currentUnixSeconds);
    }
}
