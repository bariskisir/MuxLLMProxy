using System.Text.Json;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Parses ChatGPT usage/limit response payloads into normalized snapshots.
/// </summary>
internal static class ChatGptLimitParser
{
    /// <summary>
    /// Parses the best available limit snapshot from a ChatGPT usage response.
    /// </summary>
    /// <param name="body">The raw usage response body.</param>
    /// <returns>The best matching limit snapshot, or <see langword="null"/> when none is available.</returns>
    public static ProviderLimitSnapshot? ParseBestLimitSnapshot(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var snapshots = new List<ProviderLimitSnapshot>();

        if (document.RootElement.TryGetProperty("rate_limit", out var rateLimitElement))
        {
            TryAddSnapshot(snapshots, "Limit", rateLimitElement);
        }

        if (document.RootElement.TryGetProperty("code_review_rate_limit", out var codeReviewRateLimitElement))
        {
            TryAddSnapshot(snapshots, "Code Review", codeReviewRateLimitElement);
        }

        if (document.RootElement.TryGetProperty("additional_rate_limits", out var additionalRateLimitsElement)
            && additionalRateLimitsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var additionalLimit in additionalRateLimitsElement.EnumerateArray())
            {
                var label = additionalLimit.TryGetProperty("limit_name", out var limitNameElement)
                    ? limitNameElement.GetString() ?? "Limit"
                    : "Limit";
                if (additionalLimit.TryGetProperty("rate_limit", out var additionalRateLimit))
                {
                    TryAddSnapshot(snapshots, label, additionalRateLimit);
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.WindowDurationMins == ProxyConstants.Defaults.WeeklyLimitWindowMinutes ? 0 : 1)
            .ThenBy(snapshot => snapshot.WindowDurationMins)
            .FirstOrDefault();
    }

    /// <summary>
    /// Attempts to extract a limit snapshot from a rate-limit JSON element and adds it to the collection.
    /// </summary>
    /// <param name="snapshots">The collection to populate.</param>
    /// <param name="label">The human-readable label for the limit.</param>
    /// <param name="rateLimitElement">The rate-limit JSON element.</param>
    private static void TryAddSnapshot(ICollection<ProviderLimitSnapshot> snapshots, string label, JsonElement rateLimitElement)
    {
        if (rateLimitElement.ValueKind != JsonValueKind.Object
            || !rateLimitElement.TryGetProperty("primary_window", out var primaryWindow)
            || primaryWindow.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var windowDurationMins = TryGetWindowDurationMins(primaryWindow);
        var usedPercent = TryGetInt32(primaryWindow, "used_percent");
        var resetsAt = TryGetInt64(primaryWindow, "reset_at");
        if (windowDurationMins is null || usedPercent is null || resetsAt is null)
        {
            return;
        }

        snapshots.Add(new ProviderLimitSnapshot(
            windowDurationMins == ProxyConstants.Defaults.WeeklyLimitWindowMinutes ? ProxyConstants.Labels.WeeklyLimit : label,
            Math.Max(0, ProxyConstants.Defaults.WeeklyLimitPercent - usedPercent.Value),
            usedPercent.Value,
            resetsAt.Value,
            windowDurationMins.Value));
    }

    /// <summary>
    /// Extracts the window duration in minutes from a primary window JSON element.
    /// </summary>
    /// <param name="window">The window JSON element.</param>
    /// <returns>The duration in minutes, or <see langword="null"/>.</returns>
    private static int? TryGetWindowDurationMins(JsonElement window)
    {
        var rawSeconds = TryGetInt64(window, "limit_window_seconds");
        if (rawSeconds is null || rawSeconds <= 0)
        {
            return null;
        }

        return (int)Math.Ceiling(rawSeconds.Value / 60d);
    }

    /// <summary>
    /// Attempts to parse an integer from a JSON property, handling both numeric and string values.
    /// </summary>
    /// <param name="element">The parent JSON element.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The parsed integer, or <see langword="null"/>.</returns>
    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse a long integer from a JSON property, handling both numeric and string values.
    /// </summary>
    /// <param name="element">The parent JSON element.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The parsed long integer, or <see langword="null"/>.</returns>
    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
