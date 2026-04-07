using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines mutable access to persisted provider accounts.
/// </summary>
public interface IAccountStore
{
    /// <summary>
    /// Returns the current account snapshot.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current accounts.</returns>
    Task<IReadOnlyList<Account>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves or replaces an account in the persisted store.
    /// </summary>
    /// <param name="account">The account to save.</param>
    /// <param name="replaceProviderAccounts">When <see langword="true"/>, replaces all existing accounts for the same provider type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    Task SaveAsync(Account account, bool replaceProviderAccounts, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the persisted account snapshot.
    /// </summary>
    /// <param name="accounts">The accounts to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    Task ReplaceAsync(IReadOnlyList<Account> accounts, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the temporary rate-limit cooldown for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="retryAt">The retry timestamp in UTC.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the account does not exist.</exception>
    Task MarkRateLimitedAsync(string accountId, DateTimeOffset retryAt, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the temporary rate-limit cooldown for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the account does not exist.</exception>
    Task ClearRateLimitAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Updates authentication state for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="access">The new access token.</param>
    /// <param name="refresh">The new refresh token.</param>
    /// <param name="expire">The new expiration timestamp in Unix milliseconds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the account does not exist.</exception>
    Task UpdateAuthenticationAsync(string accountId, string? access, string? refresh, long? expire, CancellationToken cancellationToken);
}
