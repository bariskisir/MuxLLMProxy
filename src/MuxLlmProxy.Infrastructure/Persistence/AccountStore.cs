using System.Collections.Immutable;
using System.Threading;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Persistence;

/// <summary>
/// Provides thread-safe in-memory access to persisted accounts.
/// </summary>
public sealed class AccountStore : IAccountStore
{
    private readonly IFileRepository _fileRepository;
    private readonly string _accountsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<Account> _accounts = [];
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccountStore"/> class.
    /// </summary>
    /// <param name="fileRepository">The file repository dependency.</param>
    /// <param name="accountsPath">The account file path.</param>
    public AccountStore(IFileRepository fileRepository, string accountsPath)
    {
        _fileRepository = fileRepository;
        _accountsPath = accountsPath;
    }

    /// <summary>
    /// Returns the current account snapshot.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current accounts.</returns>
    public async Task<IReadOnlyList<Account>> GetAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _accounts;
    }

    /// <summary>
    /// Saves or replaces an account in the persisted store.
    /// </summary>
    /// <param name="account">The account to save.</param>
    /// <param name="replaceProviderAccounts">When <see langword="true"/>, replaces all existing accounts for the same provider type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    public async Task SaveAsync(Account account, bool replaceProviderAccounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var nextAccounts = _accounts
                .Where(existing => replaceProviderAccounts
                    ? !string.Equals(existing.ProviderType, account.ProviderType, StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(existing.Id, account.Id, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray()
                .Add(account);

            _accounts = nextAccounts;
            await _fileRepository.WriteJsonAsync(_accountsPath, _accounts, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the persisted account snapshot.
    /// </summary>
    /// <param name="accounts">The accounts to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    public async Task ReplaceAsync(IReadOnlyList<Account> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _accounts = [.. accounts];
            await _fileRepository.WriteJsonAsync(_accountsPath, _accounts, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sets the temporary rate-limit cooldown for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="retryAt">The retry timestamp in UTC.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    public async Task MarkRateLimitedAsync(string accountId, DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        await UpdateAsync(accountId, account => account with { AvailableAtRateLimit = retryAt.ToUnixTimeSeconds() }, cancellationToken);
    }

    /// <summary>
    /// Clears the temporary rate-limit cooldown for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    public async Task ClearRateLimitAsync(string accountId, CancellationToken cancellationToken)
    {
        await UpdateAsync(accountId, account => account with { AvailableAtRateLimit = null }, cancellationToken);
    }

    /// <summary>
    /// Updates authentication state for an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="access">The new access token.</param>
    /// <param name="refresh">The new refresh token.</param>
    /// <param name="expire">The new expiration timestamp in Unix milliseconds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    public async Task UpdateAuthenticationAsync(string accountId, string? access, string? refresh, long? expire, CancellationToken cancellationToken)
    {
        await UpdateAsync(accountId, account => account with { Access = access, Refresh = refresh, Expire = expire }, cancellationToken);
    }

    /// <summary>
    /// Ensures the account snapshot is loaded from disk.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when loading finishes.</returns>
    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            if (!_fileRepository.Exists(_accountsPath))
            {
                _accounts = [];
                _loaded = true;
                return;
            }

            var accounts = await _fileRepository.ReadJsonAsync<List<Account>>(_accountsPath, cancellationToken);
            _accounts = [.. accounts.Select(NormalizeAccount)];
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a point update to an account and persists the new snapshot.
    /// </summary>
    /// <param name="accountId">The target account identifier.</param>
    /// <param name="mutator">The update function.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when persistence is updated.</returns>
    private async Task UpdateAsync(string accountId, Func<Account, Account> mutator, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var index = -1;
            for (var candidateIndex = 0; candidateIndex < _accounts.Length; candidateIndex++)
            {
                if (string.Equals(_accounts[candidateIndex].Id, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    index = candidateIndex;
                    break;
                }
            }
            if (index < 0)
            {
                throw new InvalidOperationException($"Account '{accountId}' was not found.");
            }

            _accounts = _accounts.SetItem(index, mutator(_accounts[index]));
            await _fileRepository.WriteJsonAsync(_accountsPath, _accounts, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Account NormalizeAccount(Account account)
    {
        return account with
        {
            AvailableAtRateLimit = NormalizeUnixTimestamp(account.AvailableAtRateLimit),
            AvailableAtWeeklyLimit = NormalizeUnixTimestamp(account.AvailableAtWeeklyLimit)
        };
    }

    private static long? NormalizeUnixTimestamp(long? value)
    {
        if (value is null)
        {
            return null;
        }

        return value > 100_000_000_000
            ? value / 1000
            : value;
    }
}
