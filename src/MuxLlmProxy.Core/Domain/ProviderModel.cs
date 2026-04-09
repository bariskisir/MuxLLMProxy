namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a model exposed by a provider.
/// </summary>
/// <param name="Id">The provider-visible model identifier.</param>
/// <param name="Name">The user-facing model display name.</param>
/// <param name="Aliases">Optional alternate identifiers that should resolve to this model.</param>
/// <param name="SupportsXHigh">Indicates whether the model supports xhigh reasoning effort.</param>
/// <param name="SupportsNone">Indicates whether the model supports none reasoning effort.</param>
public sealed record ProviderModel(
    string Id,
    string Name,
    IReadOnlyList<string>? Aliases = null,
    bool SupportsXHigh = false,
    bool SupportsNone = false);
