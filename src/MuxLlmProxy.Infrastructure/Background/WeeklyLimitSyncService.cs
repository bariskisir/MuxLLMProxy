using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Background;

/// <summary>
/// Periodically refreshes weekly-limit availability windows from providers that expose live limits.
/// </summary>
public sealed class WeeklyLimitSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(ProxyConstants.Defaults.WeeklyLimitSyncIntervalMinutes);
    private readonly IAccountStore _accountStore;
    private readonly IProviderCatalog _providerCatalog;
    private readonly IProviderAdapterResolver _providerAdapterResolver;
    private readonly ILogger<WeeklyLimitSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WeeklyLimitSyncService"/> class.
    /// </summary>
    /// <param name="accountStore">The account store dependency.</param>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    /// <param name="providerAdapterResolver">The provider adapter resolver dependency.</param>
    /// <param name="logger">The logger dependency.</param>
    public WeeklyLimitSyncService(
        IAccountStore accountStore,
        IProviderCatalog providerCatalog,
        IProviderAdapterResolver providerAdapterResolver,
        ILogger<WeeklyLimitSyncService> logger)
    {
        _accountStore = accountStore;
        _providerCatalog = providerCatalog;
        _providerAdapterResolver = providerAdapterResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncAsync(stoppingToken);

        using var timer = new PeriodicTimer(SyncInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Orchestrates the synchronization of account limits for all tracked providers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            var providerMap = (await _providerCatalog.GetAsync(cancellationToken))
                .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
            var accounts = await _accountStore.GetAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nextAccounts = await Task.WhenAll(accounts.Select(account => SyncAccountAsync(account, providerMap, now, cancellationToken)));
            var changed = nextAccounts.Where((nextAccount, index) => !Equals(nextAccount, accounts[index])).Any();

            if (changed)
            {
                await _accountStore.ReplaceAsync(nextAccounts, cancellationToken);
                _logger.LogInformation("Weekly limit sync updated account availability.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Weekly limit sync failed.");
        }
    }

    /// <summary>
    /// Synchronizes the weekly limit metadata for a single account.
    /// </summary>
    /// <param name="account">The account to sync.</param>
    /// <param name="providerMap">The provider type lookup table.</param>
    /// <param name="now">The current Unix timestamp in seconds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns> The updated account model.</returns>
    private async Task<Account> SyncAccountAsync(
        Account account,
        IReadOnlyDictionary<string, ProviderType> providerMap,
        long now,
        CancellationToken cancellationToken)
    {
        if (!account.Enabled
            || !providerMap.TryGetValue(account.ProviderType, out var providerType)
            || !providerType.TracksAvailabilityWindows)
        {
            return account;
        }

        var nextAvailableAtWeeklyLimit = account.AvailableAtWeeklyLimit is long currentWeeklyLimit && currentWeeklyLimit <= now
            ? null
            : account.AvailableAtWeeklyLimit;

        try
        {
            var adapter = _providerAdapterResolver.Resolve(providerType.Id);
            var snapshot = await adapter.GetLimitSnapshotAsync(new ProxyTarget(account, providerType), cancellationToken);
            nextAvailableAtWeeklyLimit = snapshot is not null
                && snapshot.WindowDurationMins == ProxyConstants.Defaults.WeeklyLimitWindowMinutes
                && snapshot.LeftPercent <= 0
                && snapshot.ResetsAt > now
                    ? snapshot.ResetsAt
                    : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Weekly limit sync failed for account {AccountId}", account.Id);
        }

        return nextAvailableAtWeeklyLimit == account.AvailableAtWeeklyLimit
            ? account
            : account with { AvailableAtWeeklyLimit = nextAvailableAtWeeklyLimit };
    }
}
