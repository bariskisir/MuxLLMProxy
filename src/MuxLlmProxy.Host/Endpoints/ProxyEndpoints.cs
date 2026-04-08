using System.Text.Json;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Host.Extensions;

namespace MuxLlmProxy.Host.Endpoints;

/// <summary>
/// Maps the HTTP endpoints exposed by the proxy host.
/// </summary>
public static class ProxyEndpoints
{
    /// <summary>
    /// Maps the proxy API endpoints onto the supplied route builder.
    /// </summary>
    /// <param name="app">The route builder.</param>
    public static void MapProxyEndpoints(this WebApplication app)
    {
        app.MapGet(ProxyConstants.Routes.Health, () => Results.Ok(new { status = ProxyConstants.Responses.Healthy }));

        app.MapGet(ProxyConstants.Routes.Models, async (IModelsQueryService modelsQueryService, CancellationToken cancellationToken) =>
        {
            var models = await modelsQueryService.GetModelsAsync(cancellationToken);
            return Results.Ok(new ModelsResponse(models));
        });

        app.MapGet(ProxyConstants.Routes.Limits, async (HttpContext context, IProxyConfigurationProvider configurationProvider, ILimitsQueryService limitsQueryService, CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, configurationProvider))
            {
                await context.WriteErrorAsync(StatusCodes.Status401Unauthorized, "auth_error", ProxyConstants.Messages.AuthRequired);
                return;
            }

            var limits = await limitsQueryService.GetLimitsAsync(cancellationToken);
            await context.Response.WriteAsJsonAsync(new LimitsResponse(limits), cancellationToken);
        });

        app.MapPost(ProxyConstants.Routes.Messages, HandleProxyRequestAsync);
        app.MapPost(ProxyConstants.Routes.ChatCompletions, HandleProxyRequestAsync);
    }

    /// <summary>
    /// Handles a proxied LLM request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="configurationProvider">The bound proxy configuration provider.</param>
    /// <param name="parser">The request parser.</param>
    /// <param name="orchestrator">The proxy orchestrator.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the response is written.</returns>
    private static async Task HandleProxyRequestAsync(
        HttpContext context,
        IProxyConfigurationProvider configurationProvider,
        IProxyRequestParser parser,
        IProxyOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(context, configurationProvider))
        {
            await context.WriteErrorAsync(StatusCodes.Status401Unauthorized, "auth_error", ProxyConstants.Messages.AuthRequired);
            return;
        }

        try
        {
            using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream, cancellationToken);
            ProxyFormat? formatHint = context.Request.Path.Equals(ProxyConstants.Routes.Messages, StringComparison.OrdinalIgnoreCase)
                ? ProxyFormat.Anthropic
                : context.Request.Path.Equals(ProxyConstants.Routes.ChatCompletions, StringComparison.OrdinalIgnoreCase)
                    ? ProxyFormat.OpenAi
                    : null;
            var request = await parser.ParseAsync(memoryStream.ToArray(), formatHint, cancellationToken);
            var response = await orchestrator.ExecuteAsync(request, cancellationToken);
            await context.WriteProxyResponseAsync(response);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is JsonException || exception is ArgumentException)
        {
            if (!context.Response.HasStarted)
            {
                await context.WriteErrorAsync(StatusCodes.Status400BadRequest, "validation_error", exception);
            }
        }
        catch (HttpRequestException exception)
        {
            if (!context.Response.HasStarted)
            {
                await context.WriteErrorAsync(StatusCodes.Status502BadGateway, "upstream_error", exception);
            }
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                await context.WriteErrorAsync(StatusCodes.Status504GatewayTimeout, "timeout_error", exception);
            }
        }
    }

    /// <summary>
    /// Determines whether the current request is authorized.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="configurationProvider">The bound proxy configuration provider.</param>
    /// <returns><see langword="true"/> when authorization succeeds; otherwise <see langword="false"/>.</returns>
    private static async Task<bool> IsAuthorizedAsync(HttpContext context, IProxyConfigurationProvider configurationProvider)
    {
        var token = (await configurationProvider.GetAsync(context.RequestAborted)).Token;

        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (!context.Request.Headers.TryGetValue(ProxyConstants.Headers.Authorization, out var authorizationHeader))
        {
            return false;
        }

        return string.Equals(
            authorizationHeader.ToString(),
            $"{ProxyConstants.Responses.BearerScheme} {token}",
            StringComparison.Ordinal);
    }
}
