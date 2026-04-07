using MuxLlmProxy.Core.Abstractions;

namespace MuxLlmProxy.Infrastructure.Proxy;

/// <summary>
/// Resolves provider adapters by provider identifier.
/// </summary>
public sealed class ProviderAdapterResolver : IProviderAdapterResolver
{
    private readonly IReadOnlyDictionary<string, IProviderAdapter> _adapters;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderAdapterResolver"/> class.
    /// </summary>
    /// <param name="adapters">The registered adapters.</param>
    public ProviderAdapterResolver(IEnumerable<IProviderAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the provider adapter for the supplied provider identifier.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The resolved adapter.</returns>
    public IProviderAdapter Resolve(string providerId)
    {
        if (_adapters.TryGetValue(providerId, out var adapter))
        {
            return adapter;
        }

        throw new KeyNotFoundException($"No provider adapter is registered for '{providerId}'.");
    }
}
