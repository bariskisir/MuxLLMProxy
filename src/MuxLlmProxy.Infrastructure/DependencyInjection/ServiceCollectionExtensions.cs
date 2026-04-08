using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Services;
using MuxLlmProxy.Infrastructure.Background;
using MuxLlmProxy.Infrastructure.Persistence;
using MuxLlmProxy.Infrastructure.Providers.ChatGpt;
using MuxLlmProxy.Infrastructure.Providers.OpenAiCompatible;
using MuxLlmProxy.Infrastructure.Proxy;
using MuxLlmProxy.Infrastructure.Translation;

namespace MuxLlmProxy.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the infrastructure and application services required by the proxy host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers proxy dependencies for the supplied configuration and data paths.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="accountsPath">The accounts file path.</param>
    /// <param name="modelsPath">The models file path.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddMuxLlmProxy(
        this IServiceCollection services,
        IConfiguration configuration,
        string accountsPath,
        string modelsPath)
    {
        var options = configuration.GetSection("ProxySettings").Get<ProxyOptions>() ?? new ProxyOptions();

        services.Configure<ProxyOptions>(configuration.GetSection("ProxySettings"));
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<IProxyConfigurationProvider, ProxyConfigurationProvider>();
        services.AddSingleton<IProviderCatalog>(provider =>
            new ProviderCatalog(provider.GetRequiredService<IFileRepository>(), modelsPath));
        services.AddSingleton<IAccountStore>(provider =>
            new AccountStore(provider.GetRequiredService<IFileRepository>(), accountsPath));

        services.AddSingleton<IRoundRobinState, RoundRobinState>();
        services.AddSingleton<IMessageTranslator, AnthropicMessageTranslator>();
        services.AddSingleton<IProxyRequestParser, ProxyRequestParser>();
        services.AddSingleton<IModelsQueryService, ModelsQueryService>();
        services.AddSingleton<ILimitsQueryService, LimitsQueryService>();
        services.AddSingleton<ITargetSelector, TargetSelector>();
        services.AddSingleton<IProxyOrchestrator, ProxyOrchestrator>();

        services.AddSingleton<IProviderAdapter, ChatGptAdapter>();
        services.AddSingleton<IProviderAdapter>(provider =>
            new OpenAiCompatibleAdapter(ProxyConstants.Providers.OpenRouter, provider.GetRequiredService<IMessageTranslator>()));
        services.AddSingleton<IProviderAdapter>(provider =>
            new OpenAiCompatibleAdapter(ProxyConstants.Providers.OpenCode, provider.GetRequiredService<IMessageTranslator>()));
        services.AddSingleton<IProviderAdapterResolver, ProviderAdapterResolver>();
        services.AddHostedService<WeeklyLimitSyncService>();

        services.AddHttpClient("upstream", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        return services;
    }
}
