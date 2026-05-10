using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Services;

/// <summary>
/// Provides limit information for configured accounts.
/// </summary>
public sealed class LimitsQueryService : ILimitsQueryService
{
    private readonly IAccountStore _accountStore;
    private readonly IProviderCatalog _providerCatalog;
    private readonly IProviderAdapterResolver _providerAdapterResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="LimitsQueryService"/> class.
    /// </summary>
    /// <param name="accountStore">The account store dependency.</param>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    /// <param name="providerAdapterResolver">The adapter resolver dependency.</param>
    public LimitsQueryService(IAccountStore accountStore, IProviderCatalog providerCatalog, IProviderAdapterResolver providerAdapterResolver)
    {
        _accountStore = accountStore;
        _providerCatalog = providerCatalog;
        _providerAdapterResolver = providerAdapterResolver;
    }

    /// <summary>
    /// Returns limit entries for all configured accounts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The limit entries.</returns>
    public async Task<IReadOnlyList<LimitEntry>> GetLimitsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountStore.GetAsync(cancellationToken);
        var providerTypes = await _providerCatalog.GetAsync(cancellationToken);
        var providerMap = providerTypes.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var entryTasks = accounts.Select(async account =>
        {
            if (!providerMap.TryGetValue(account.ProviderType, out var providerType))
            {
                return null;
            }

            var entry = new LimitEntry
            {
                TypeId = providerType.Id,
                Id = account.Id,
                HasLimits = providerType.TracksAvailabilityWindows,
                Token = account.Access
            };

            ProviderLimitSnapshot? snapshot = null;
            if (providerType.TracksAvailabilityWindows)
            {
                try
                {
                    var adapter = _providerAdapterResolver.Resolve(providerType.Id);
                    snapshot = await adapter.GetLimitSnapshotAsync(new ProxyTarget(account, providerType), cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return entry with
                    {
                        Failed = true
                    };
                }
            }

            return entry with
            {
                Limit = snapshot
            };
        });

        var entries = await Task.WhenAll(entryTasks);
        return entries
            .Where(entry => entry is not null)
            .Cast<LimitEntry>()
            .ToArray();
    }
}
