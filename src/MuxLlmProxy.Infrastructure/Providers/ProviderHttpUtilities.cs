using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers;

/// <summary>
/// Provides shared provider HTTP helper methods.
/// </summary>
internal static class ProviderHttpUtilities
{
    public static Dictionary<string, string> CreateJsonHeaders(string contentType)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ProxyConstants.Headers.ContentType] = contentType
        };
    }

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
