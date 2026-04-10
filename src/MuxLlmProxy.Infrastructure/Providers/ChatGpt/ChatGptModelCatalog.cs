using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Resolves and normalizes model identifiers and reasoning parameters for the ChatGPT provider.
/// </summary>
public sealed class ChatGptModelCatalog
{
    private readonly IProviderCatalog _providerCatalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGptModelCatalog"/> class.
    /// </summary>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    public ChatGptModelCatalog(IProviderCatalog providerCatalog)
    {
        _providerCatalog = providerCatalog;
    }

    /// <summary>
    /// Resolves and normalizes the requested model identifier against the ChatGPT model catalog.
    /// </summary>
    /// <param name="requestedModel">The raw model identifier from the request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized model identifier.</returns>
    public async Task<string> NormalizeModelAsync(string? requestedModel, CancellationToken cancellationToken)
    {
        var modelId = ExtractModelId(requestedModel);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("The request did not include a model identifier.");
        }

        var model = await ResolveModelAsync(modelId, cancellationToken);
        return model?.Id ?? modelId;
    }

    /// <summary>
    /// Normalizes the reasoning effort parameter based on model capabilities.
    /// </summary>
    /// <param name="modelId">The normalized model identifier.</param>
    /// <param name="effort">The raw reasoning effort value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The normalized reasoning effort string.</returns>
    public async Task<string> NormalizeReasoningEffortAsync(string modelId, string? effort, CancellationToken cancellationToken)
    {
        var model = await ResolveModelAsync(modelId, cancellationToken);
        var supportsXHigh = model?.SupportsXHigh ?? false;
        var supportsNone = model?.SupportsNone ?? false;

        if (string.IsNullOrWhiteSpace(effort))
        {
            return supportsXHigh ? "high" : "medium";
        }

        var normalized = effort.Trim().ToLowerInvariant();
        if (normalized == "minimal")
        {
            normalized = "low";
        }

        if (normalized == "none" && !supportsNone)
        {
            normalized = "low";
        }

        if (normalized == "xhigh" && !supportsXHigh)
        {
            normalized = "high";
        }

        return normalized;
    }

    /// <summary>
    /// Normalizes the reasoning summary parameter to a valid value.
    /// </summary>
    /// <param name="summary">The raw reasoning summary value.</param>
    /// <returns>The normalized reasoning summary string.</returns>
    public static string NormalizeReasoningSummary(string? summary)
    {
        return string.IsNullOrWhiteSpace(summary)
            ? "auto"
            : summary.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Resolves a model definition from the ChatGPT provider by its identifier or alias.
    /// </summary>
    /// <param name="requestedModel">The requested model name or alias.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved model definition, or <see langword="null"/>.</returns>
    private async Task<ProviderModel?> ResolveModelAsync(string requestedModel, CancellationToken cancellationToken)
    {
        var providerTypes = await _providerCatalog.GetAsync(cancellationToken);
        var chatGptProvider = providerTypes.FirstOrDefault(provider =>
            string.Equals(provider.Id, ProxyConstants.Providers.ChatGpt, StringComparison.OrdinalIgnoreCase));

        return chatGptProvider?.Models.FirstOrDefault(model =>
            string.Equals(model.Id, requestedModel, StringComparison.OrdinalIgnoreCase)
            || (model.Aliases?.Any(alias => string.Equals(alias, requestedModel, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

    /// <summary>
    /// Extracts the core model identifier from a potentially qualified model name (e.g., removing provider prefixes).
    /// </summary>
    /// <param name="requestedModel">The raw requested model identifier.</param>
    /// <returns>The core model identifier.</returns>
    private static string? ExtractModelId(string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return null;
        }

        var modelId = requestedModel.Contains('/', StringComparison.Ordinal)
            ? requestedModel[(requestedModel.LastIndexOf('/') + 1)..]
            : requestedModel;

        return modelId.Trim();
    }
}
