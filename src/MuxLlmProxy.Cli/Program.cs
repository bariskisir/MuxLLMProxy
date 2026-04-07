using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MuxLlmProxy.Cli;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Infrastructure.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var dataDirectory = ProxyPathResolver.ResolveDataDirectory(Directory.GetCurrentDirectory());
var accountsPath = Path.Combine(dataDirectory, ProxyConstants.Paths.AccountsFileName);
var modelsPath = Path.Combine(dataDirectory, ProxyConstants.Paths.ModelsFileName);

var services = new ServiceCollection();
services.AddMuxLlmProxy(configuration, accountsPath, modelsPath);
services.AddSingleton<CliCommandRunner>();

using var serviceProvider = services.BuildServiceProvider();
var handled = await serviceProvider.GetRequiredService<CliCommandRunner>().TryRunAsync(args, CancellationToken.None);
Environment.ExitCode = handled ? 0 : 1;
