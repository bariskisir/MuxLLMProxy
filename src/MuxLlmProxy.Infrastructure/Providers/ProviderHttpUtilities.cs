using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers;

/// <summary>
/// Provides shared provider HTTP helper methods.
/// </summary>
internal static class ProviderHttpUtilities
{
    /// <summary>
    /// Creates a response header dictionary with the specified content type.
    /// </summary>
    /// <param name="contentType">The content type value.</param>
    /// <returns>A dictionary containing the Content-Type header.</returns>
    public static Dictionary<string, string> CreateJsonHeaders(string contentType)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ProxyConstants.Headers.ContentType] = contentType
        };
    }

    /// <summary>
    /// Creates a weekly limit snapshot from the supplied reset timestamp.
    /// </summary>
    /// <param name="resetsAt">The reset timestamp in Unix seconds, or <see langword="null"/>.</param>
    /// <returns>The limit snapshot, or <see langword="null"/> when the timestamp is absent.</returns>
    public static ProviderLimitSnapshot? CreateWeeklyLimitSnapshot(long? resetsAt)
    {
        return resetsAt is long value
            ? new ProviderLimitSnapshot(
                ProxyConstants.Labels.WeeklyLimit,
                0,
                ProxyConstants.Defaults.WeeklyLimitPercent,
                value,
                ProxyConstants.Defaults.WeeklyLimitWindowMinutes)
            : null;
    }

    /// <summary>
    /// Parses a raw custom headers string into key-value pairs.
    /// </summary>
    /// <param name="rawHeaders">The raw header string with entries separated by newlines or semicolons.</param>
    /// <returns>An enumerable of header key-value pairs.</returns>
    public static IEnumerable<KeyValuePair<string, string>> ParseCustomHeaders(string? rawHeaders)
    {
        if (string.IsNullOrWhiteSpace(rawHeaders))
        {
            yield break;
        }

        foreach (var entry in rawHeaders.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = entry.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == entry.Length - 1)
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(
                entry[..separatorIndex].Trim(),
                entry[(separatorIndex + 1)..].Trim());
        }
    }
}
