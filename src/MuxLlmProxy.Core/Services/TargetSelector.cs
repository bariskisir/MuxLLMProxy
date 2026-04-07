using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Services;

/// <summary>
/// Selects provider targets for a requested model.
/// </summary>
public sealed class TargetSelector : ITargetSelector
{
    private readonly IProviderCatalog _providerCatalog;
    private readonly IAccountStore _accountStore;
    private readonly IRoundRobinState _roundRobinState;

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetSelector"/> class.
    /// </summary>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    /// <param name="accountStore">The account store dependency.</param>
    /// <param name="roundRobinState">The round-robin state dependency.</param>
    public TargetSelector(IProviderCatalog providerCatalog, IAccountStore accountStore, IRoundRobinState roundRobinState)
    {
        _providerCatalog = providerCatalog;
        _accountStore = accountStore;
        _roundRobinState = roundRobinState;
    }

    /// <summary>
    /// Resolves eligible targets for a requested model.
    /// </summary>
    /// <param name="requestedModel">The requested model identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ordered eligible targets.</returns>
    public async Task<IReadOnlyList<ProxyTarget>> SelectAsync(string requestedModel, CancellationToken cancellationToken)
    {
        var providerTypes = await _providerCatalog.GetAsync(cancellationToken);
        var accounts = await _accountStore.GetAsync(cancellationToken);
        var providerMap = providerTypes.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var grouped = new Dictionary<string, List<ProxyTarget>>(StringComparer.OrdinalIgnoreCase);
        var providerOrder = new List<string>();

        foreach (var account in accounts)
        {
            if (!account.Enabled || !providerMap.TryGetValue(account.ProviderType, out var providerType) || !providerType.IsAccountAvailable(account, now))
            {
                continue;
            }

            if (!providerType.Models.Any(model => string.Equals(model.Id, requestedModel, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!grouped.TryGetValue(providerType.Id, out var targets))
            {
                targets = new List<ProxyTarget>();
                grouped[providerType.Id] = targets;
                providerOrder.Add(providerType.Id);
            }

            targets.Add(new ProxyTarget(account, providerType));
        }

        var result = new List<ProxyTarget>();
        foreach (var providerId in providerOrder)
        {
            var targets = grouped[providerId];
            if (targets.Count == 0)
            {
                continue;
            }

            var providerType = targets[0].ProviderType;
            if (!providerType.SupportsMulti)
            {
                result.Add(targets[0]);
                continue;
            }

            var key = $"{requestedModel}:{providerId}";
            var offset = _roundRobinState.GetOffset(key, targets.Count);
            for (var index = 0; index < targets.Count; index++)
            {
                result.Add(targets[(offset + index) % targets.Count]);
            }
        }

        return result;
    }
}
