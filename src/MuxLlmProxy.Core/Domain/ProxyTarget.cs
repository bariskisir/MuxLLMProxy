namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a resolved provider target composed of an account and its provider definition.
/// </summary>
/// <param name="Account">The selected account.</param>
/// <param name="ProviderType">The provider type metadata.</param>
public sealed record ProxyTarget(Account Account, ProviderType ProviderType);
