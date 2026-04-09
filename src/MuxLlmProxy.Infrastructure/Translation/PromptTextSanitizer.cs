using System.Text.RegularExpressions;

namespace MuxLlmProxy.Infrastructure.Translation;

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

    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return SystemReminderRegex().Replace(text, string.Empty).Trim();
    }

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

    public static bool ContainsClientHarness(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ClientHarnessMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
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
