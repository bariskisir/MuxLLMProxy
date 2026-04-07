using System.Text.Json;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Host.Extensions;

/// <summary>
/// Provides HTTP response helpers for the host layer.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Writes a proxy response to the current HTTP response.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="response">The proxy response to write.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public static async Task WriteProxyResponseAsync(this HttpContext context, ProxyResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        await context.Response.Body.WriteAsync(response.Body);
    }

    /// <summary>
    /// Writes a normalized JSON error response.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="type">The normalized error type.</param>
    /// <param name="message">The error message.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public static Task WriteErrorAsync(this HttpContext context, int statusCode, string type, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ProxyConstants.ContentTypes.Json;
        var payload = new ApiErrorEnvelope(new ApiError
        {
            Type = type,
            Message = message
        });

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    public static Task WriteErrorAsync(this HttpContext context, int statusCode, string type, Exception exception)
    {
        var environment = context.RequestServices.GetService<IWebHostEnvironment>();
        var message = environment?.IsDevelopment() == true
            ? exception.ToString()
            : statusCode >= StatusCodes.Status500InternalServerError
                ? "The request could not be completed."
                : exception.Message;

        return WriteErrorAsync(context, statusCode, type, message);
    }
}
