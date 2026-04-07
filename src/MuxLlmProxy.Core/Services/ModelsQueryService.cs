using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Services;

/// <summary>
/// Provides model discovery for enabled accounts.
/// </summary>
public sealed class ModelsQueryService : IModelsQueryService
{
    private readonly IProviderCatalog _providerCatalog;
    private readonly IAccountStore _accountStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelsQueryService"/> class.
    /// </summary>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    /// <param name="accountStore">The account store dependency.</param>
    public ModelsQueryService(IProviderCatalog providerCatalog, IAccountStore accountStore)
    {
        _providerCatalog = providerCatalog;
        _accountStore = accountStore;
    }

    /// <summary>
    /// Returns the distinct models exposed by enabled accounts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exposed models sorted by identifier.</returns>
    public async Task<IReadOnlyList<ProviderModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var providerTypes = await _providerCatalog.GetAsync(cancellationToken);
        var accounts = await _accountStore.GetAsync(cancellationToken);
        var enabledProviderIds = accounts.Where(account => account.Enabled)
            .Select(account => account.ProviderType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return providerTypes
            .Where(providerType => enabledProviderIds.Contains(providerType.Id))
            .SelectMany(providerType => providerType.Models)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
