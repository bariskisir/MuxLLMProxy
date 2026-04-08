using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Host.Endpoints;
using MuxLlmProxy.Host.Logging;
using MuxLlmProxy.Infrastructure.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace MuxLlmProxy.Host;

/// <summary>
/// Provides the application entry point for the proxy host.
/// </summary>
public static class Program
{
    /// <summary>
    /// Starts the ASP.NET Core proxy host.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>A task that completes when the host stops.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required configuration is missing.</exception>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var proxyOptions = builder.Configuration.GetSection("ProxySettings").Get<ProxyOptions>()
            ?? throw new InvalidOperationException("ProxySettings configuration is required.");

        builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection("ProxySettings"));
        var effectiveTimeout = proxyOptions.GetNormalizedTimeoutSeconds();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(effectiveTimeout);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(effectiveTimeout);
        });

        builder.Services.AddHttpContextAccessor();

        var dataDirectory = ProxyPathResolver.ResolveDataDirectory(builder.Environment.ContentRootPath);
        var accountsPath = Path.Combine(dataDirectory, ProxyConstants.Paths.AccountsFileName);
        var modelsPath = Path.Combine(dataDirectory, ProxyConstants.Paths.ModelsFileName);
        var logsDirectory = Path.Combine(dataDirectory, ProxyConstants.Paths.LogsDirectoryName);
        Directory.CreateDirectory(logsDirectory);

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .MinimumLevel.Warning()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(logsDirectory, ProxyConstants.Paths.ProxyLogFilePattern),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: ProxyConstants.Defaults.LogRetentionDays,
                    shared: true);

            if (context.HostingEnvironment.IsDevelopment())
            {
                configuration.WriteTo.Console();
            }
        });

        builder.Services.AddMuxLlmProxy(builder.Configuration, accountsPath, modelsPath);
        builder.WebHost.UseUrls($"http://*:{proxyOptions.Port}");

        var app = builder.Build();

        app.UseMiddleware<ProxyLoggingMiddleware>();
        app.MapProxyEndpoints();
        await app.RunAsync();
    }
}
