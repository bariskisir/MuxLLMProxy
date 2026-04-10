using MuxLlmProxy.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text;

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
        var requestBody = await ReadRequestBodyAsync(context);

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
                    Headers = HttpLoggingSanitizer.SanitizeHeaders(context.Request.Headers.Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString()))),
                    Body = requestBody
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

    /// <summary>
    /// Reads the inbound request body bytes into a string, ensuring the stream is repositioned for subsequent middleware.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The request body string.</returns>
    private static async Task<string> ReadRequestBodyAsync(HttpContext context)
    {
        if (context.Request.Body is null || !context.Request.Body.CanRead)
        {
            return string.Empty;
        }

        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;

        using var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);

        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        return body;
    }
}
