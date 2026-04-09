using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

public sealed class ChatGptModelCatalog
{
    private readonly IProviderCatalog _providerCatalog;

    public ChatGptModelCatalog(IProviderCatalog providerCatalog)
    {
        _providerCatalog = providerCatalog;
    }

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

    public static string NormalizeReasoningSummary(string? summary)
    {
        return string.IsNullOrWhiteSpace(summary)
            ? "auto"
            : summary.Trim().ToLowerInvariant();
    }

    private async Task<ProviderModel?> ResolveModelAsync(string requestedModel, CancellationToken cancellationToken)
    {
        var providerTypes = await _providerCatalog.GetAsync(cancellationToken);
        var chatGptProvider = providerTypes.FirstOrDefault(provider =>
            string.Equals(provider.Id, ProxyConstants.Providers.ChatGpt, StringComparison.OrdinalIgnoreCase));

        return chatGptProvider?.Models.FirstOrDefault(model =>
            string.Equals(model.Id, requestedModel, StringComparison.OrdinalIgnoreCase)
            || (model.Aliases?.Any(alias => string.Equals(alias, requestedModel, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

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
