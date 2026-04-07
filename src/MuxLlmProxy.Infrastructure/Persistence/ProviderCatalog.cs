using System.Collections.Immutable;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Persistence;

/// <summary>
/// Provides the built-in provider type catalog with model definitions loaded from disk.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog
{
    private static readonly IReadOnlyList<ProviderType> ProviderTemplates =
    [
        new ProviderType
        {
            Id = ProxyConstants.Providers.OpenRouter,
            BaseUrl = "https://openrouter.ai/api/v1",
            HasLimits = false,
            SupportsMulti = false,
            Models = []
        },
        new ProviderType
        {
            Id = ProxyConstants.Providers.ChatGpt,
            BaseUrl = "https://chatgpt.com/",
            HasLimits = true,
            SupportsMulti = true,
            Models = []
        },
        new ProviderType
        {
            Id = ProxyConstants.Providers.OpenCode,
            BaseUrl = "https://opencode.ai/zen/v1",
            CustomHeaders = $"{ProxyConstants.Headers.OpenCodeSession}: {ProxyConstants.ProviderHeaders.OpenCodeSessionValue}",
            HasLimits = false,
            SupportsMulti = false,
            Models = []
        }
    ];

    private readonly IFileRepository _fileRepository;
    private readonly string _modelsPath;
    private ImmutableArray<ProviderType> _providerTypes = [];
    private bool _loaded;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalog"/> class.
    /// </summary>
    /// <param name="fileRepository">The file repository dependency.</param>
    /// <param name="modelsPath">The models file path.</param>
    public ProviderCatalog(IFileRepository fileRepository, string modelsPath)
    {
        _fileRepository = fileRepository;
        _modelsPath = modelsPath;
    }

    /// <summary>
    /// Loads the provider type catalog.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The configured provider types.</returns>
    public async Task<IReadOnlyList<ProviderType>> GetAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _providerTypes;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            if (!_fileRepository.Exists(_modelsPath))
            {
                throw new InvalidOperationException($"The provider model catalog '{_modelsPath}' was not found.");
            }

            var modelEntries = await _fileRepository.ReadJsonAsync<List<ProviderModelEntry>>(_modelsPath, cancellationToken);
            var modelsByProvider = modelEntries
                .GroupBy(entry => entry.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ProviderModel>)group
                        .Select(entry => new ProviderModel(entry.Id, entry.Name))
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            _providerTypes = [.. ProviderTemplates.Select(template =>
            {
                if (!modelsByProvider.TryGetValue(template.Id, out var models) || models.Count == 0)
                {
                    throw new InvalidOperationException($"No models were configured for provider '{template.Id}' in '{_modelsPath}'.");
                }

                return template with { Models = models };
            })];

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ProviderModelEntry
    {
        public required string ProviderId { get; init; }

        public required string Id { get; init; }

        public required string Name { get; init; }
    }
}
