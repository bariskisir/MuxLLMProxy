namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a model exposed by a provider.
/// </summary>
/// <param name="Id">The provider-visible model identifier.</param>
/// <param name="Name">The user-facing model display name.</param>
public sealed record ProviderModel(string Id, string Name);
