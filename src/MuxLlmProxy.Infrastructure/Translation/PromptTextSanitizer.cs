using System.Text.RegularExpressions;

namespace MuxLlmProxy.Infrastructure.Translation;

/// <summary>
/// Sanitizes and filters prompt text by removing client harness markers, system reminders,
/// and transient assistant planning content.
/// </summary>
internal static partial class PromptTextSanitizer
{
    private static readonly string[] HarnessMarkers =
    [
        "tool",
        "provider",
        "session",
        "response channels",
        "formatting rules"
    ];

    private static readonly string[] ClientHarnessMarkers =
    [
        "You are Claude Code",
        "Break down and manage your work with the TaskCreate tool",
        "Use AskUserQuestion",
        "Entered plan mode",
        "Plan mode is active",
        "auto memory",
        "# Using your tools",
        "# Doing tasks",
        "# Executing actions with care",
        "# Session-specific guidance"
    ];

    /// <summary>
    /// Sanitizes the supplied text by stripping system reminder tags.
    /// </summary>
    /// <param name="text">The raw text to sanitize.</param>
    /// <returns>The sanitized text.</returns>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return SystemReminderRegex().Replace(text, string.Empty).Trim();
    }

    /// <summary>
    /// Extracts the most recent line that looks like a user intent from sanitized text.
    /// </summary>
    /// <param name="text">The raw text to extract from.</param>
    /// <returns>The most recent user intent line, or the full sanitized text.</returns>
    public static string ExtractLatestUserIntent(string text)
    {
        var sanitized = Sanitize(text);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        var latestLine = sanitized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Reverse()
            .FirstOrDefault(IsLikelyUserIntentLine);

        return string.IsNullOrWhiteSpace(latestLine)
            ? sanitized
            : latestLine;
    }

    /// <summary>
    /// Sanitizes instruction text by removing client harness content entirely.
    /// </summary>
    /// <param name="text">The raw instruction text.</param>
    /// <returns>The sanitized instruction text, or empty when harness content is detected.</returns>
    public static string SanitizeInstructions(string text)
    {
        var sanitized = Sanitize(text);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        if (ContainsClientHarness(sanitized))
        {
            return string.Empty;
        }

        return sanitized;
    }

    /// <summary>
    /// Determines whether the supplied text contains known client harness markers.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns><see langword="true"/> when client harness content is detected; otherwise <see langword="false"/>.</returns>
    public static bool ContainsClientHarness(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ClientHarnessMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether the supplied assistant text should be dropped as transient planning content.
    /// This consolidates the filtering logic used by both the ChatGPT request transformer and
    /// the Anthropic message translator.
    /// </summary>
    /// <param name="text">The assistant text to inspect.</param>
    /// <returns><see langword="true"/> when the text should be dropped; otherwise <see langword="false"/>.</returns>
    public static bool ShouldDropTransientText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (ContainsClientHarness(text))
        {
            return true;
        }

        var normalized = text.Trim();
        return normalized.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("# Todos", StringComparison.Ordinal)
            || normalized.StartsWith("Preparing for", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Deciding on", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I need to", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I'm ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I\u2019m ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Plan mode", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("TaskCreate", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("todowrite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyUserIntentLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.StartsWith("<", StringComparison.Ordinal)
            || line.StartsWith("You are ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("## ", StringComparison.Ordinal)
            || line.StartsWith("- ", StringComparison.Ordinal)
            || line.StartsWith("* ", StringComparison.Ordinal))
        {
            return false;
        }

        return !HarnessMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("(?is)<system-reminder>.*?</system-reminder>")]
    private static partial Regex SystemReminderRegex();
}
