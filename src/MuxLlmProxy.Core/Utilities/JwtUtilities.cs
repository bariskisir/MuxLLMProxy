using System.Text.Json;

namespace MuxLlmProxy.Core.Utilities;

/// <summary>
/// Provides helper methods for reading JWT payload claims without validating signatures.
/// </summary>
public static class JwtUtilities
{
    /// <summary>
    /// Tries to read a nested string claim from the JWT payload.
    /// </summary>
    /// <param name="token">The JWT token.</param>
    /// <param name="path">The property path to read.</param>
    /// <returns>The claim value when found; otherwise <see langword="null"/>.</returns>
    public static string? TryReadStringClaim(string? token, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(token) || path.Length == 0)
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeSegment(segments[1]));
            var current = document.RootElement;
            foreach (var propertyName in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propertyName, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String
                ? current.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] DecodeSegment(string segment)
    {
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(normalized);
    }
}
