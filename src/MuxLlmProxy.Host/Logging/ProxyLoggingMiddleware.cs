using MuxLlmProxy.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace MuxLlmProxy.Host.Logging;

/// <summary>
/// Captures inbound proxy request and response details for structured logging.
/// </summary>
public sealed class ProxyLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ProxyLoggingMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyLoggingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware delegate.</param>
    /// <param name="logger">The logger dependency.</param>
    public ProxyLoggingMiddleware(RequestDelegate next, ILogger<ProxyLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Logs the inbound request and outbound response for the current HTTP exchange.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the pipeline finishes.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestPath"] = context.Request.Path.Value,
            ["RequestMethod"] = context.Request.Method
        }))
        {
            _logger.LogInformation(
                "Inbound proxy request {@InboundRequest}",
                new
                {
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value,
                    QueryString = context.Request.QueryString.Value,
                    Headers = HttpLoggingSanitizer.SanitizeHeaders(context.Request.Headers.Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString())))
                });

            await _next(context);

            _logger.LogInformation(
                "Inbound proxy response {@InboundResponse}",
                new
                {
                    StatusCode = context.Response.StatusCode,
                    Headers = HttpLoggingSanitizer.SanitizeHeaders(context.Response.Headers.Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString())))
                });
        }
    }
}
