namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Resolves provider adapters by provider identifier.
/// </summary>
public interface IProviderAdapterResolver
{
    /// <summary>
    /// Resolves the provider adapter for the supplied provider identifier.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The resolved provider adapter.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no adapter is registered for the provider.</exception>
    IProviderAdapter Resolve(string providerId);
}
