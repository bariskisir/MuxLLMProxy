using MuxLlmProxy.Core.Configuration;

namespace MuxLlmProxy.Core.Utilities;

/// <summary>
/// Provides helpers for redacting sensitive HTTP logging data.
/// </summary>
public static class HttpLoggingSanitizer
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ProxyConstants.Headers.Authorization,
        ProxyConstants.Headers.Cookie,
        ProxyConstants.Headers.SetCookie,
        ProxyConstants.Headers.XApiKey,
        ProxyConstants.Headers.ApiKey
    };

    /// <summary>
    /// Returns a copy of the provided headers with sensitive values redacted.
    /// </summary>
    /// <param name="headers">The headers to sanitize.</param>
    /// <returns>The sanitized headers.</returns>
    public static Dictionary<string, string> SanitizeHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        return headers.ToDictionary(
            header => header.Key,
            header => SensitiveHeaders.Contains(header.Key) ? "***REDACTED***" : header.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns log bodies with sensitive values already redacted elsewhere.
    /// </summary>
    /// <param name="body">The body text to sanitize.</param>
    /// <returns>The sanitized body text.</returns>
    public static string SanitizeBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body;
    }
}
