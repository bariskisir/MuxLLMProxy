using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuxLlmProxy.Infrastructure.Translation;

internal sealed partial class HeuristicToolParser
{
    private ParserState _state = ParserState.Text;
    private string _buffer = string.Empty;
    private string? _currentToolId;
    private string? _currentFunctionName;
    private Dictionary<string, object?> _currentParameters = new(StringComparer.Ordinal);

    public (string Text, IReadOnlyList<Dictionary<string, object?>> ToolUses) Feed(string text)
    {
        _buffer += StripControlTokens(text);
        var detectedTools = new List<Dictionary<string, object?>>();
        var webTools = ExtractWebToolJsonCalls();
        if (webTools.Count > 0)
        {
            detectedTools.AddRange(webTools);
        }

        var filteredOutputParts = new List<string>();
        while (true)
        {
            if (_state == ParserState.Text)
            {
                var markerIndex = _buffer.IndexOf("\u25cf", StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    filteredOutputParts.Add(_buffer[..markerIndex]);
                    _buffer = _buffer[markerIndex..];
                    _state = ParserState.MatchingFunction;
                }
                else
                {
                    var safePrefix = SplitIncompleteControlTokenTail();
                    if (!string.IsNullOrEmpty(safePrefix))
                    {
                        filteredOutputParts.Add(safePrefix);
                        break;
                    }

                    filteredOutputParts.Add(_buffer);
                    _buffer = string.Empty;
                    break;
                }
            }

            if (_state == ParserState.MatchingFunction)
            {
                var match = FunctionStartRegex().Match(_buffer);
                if (match.Success)
                {
                    _currentFunctionName = match.Groups[1].Value.Trim();
                    _currentToolId = ($"toolu_heuristic_{Guid.NewGuid():N}")[..24];
                    _currentParameters = new Dictionary<string, object?>(StringComparer.Ordinal);
                    _buffer = _buffer[(match.Index + match.Length)..];
                    _state = ParserState.ParsingParameters;
                }
                else if (_buffer.Length > 100)
                {
                    filteredOutputParts.Add(_buffer[..1]);
                    _buffer = _buffer[1..];
                    _state = ParserState.Text;
                }
                else
                {
                    break;
                }
            }

            if (_state == ParserState.ParsingParameters)
            {
                var finished = false;
                while (true)
                {
                    var match = ParameterRegex().Match(_buffer);
                    if (!match.Success || !match.Value.Contains("</parameter>", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (match.Index > 0)
                    {
                        filteredOutputParts.Add(_buffer[..match.Index]);
                    }

                    _currentParameters[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
                    _buffer = _buffer[(match.Index + match.Length)..];
                }

                var markerIndex = _buffer.IndexOf("\u25cf", StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    if (markerIndex > 0)
                    {
                        filteredOutputParts.Add(_buffer[..markerIndex]);
                    }

                    _buffer = _buffer[markerIndex..];
                    finished = true;
                }
                else if (_buffer.Length > 0 && !_buffer.TrimStart().StartsWith("<", StringComparison.Ordinal))
                {
                    if (!_buffer.Contains("<parameter=", StringComparison.Ordinal))
                    {
                        filteredOutputParts.Add(_buffer);
                        _buffer = string.Empty;
                        finished = true;
                    }
                }

                if (finished)
                {
                    detectedTools.Add(CreateToolUse(_currentToolId, _currentFunctionName, _currentParameters));
                    _state = ParserState.Text;
                }
                else
                {
                    break;
                }
            }
        }

        return (string.Concat(filteredOutputParts), detectedTools);
    }

    public IReadOnlyList<Dictionary<string, object?>> Flush()
    {
        _buffer = StripControlTokens(_buffer);
        var detected = new List<Dictionary<string, object?>>();
        if (_state != ParserState.ParsingParameters)
        {
            return detected;
        }

        foreach (Match match in PartialParameterRegex().Matches(_buffer))
        {
            _currentParameters[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
        }

        detected.Add(CreateToolUse(_currentToolId, _currentFunctionName, _currentParameters));
        _state = ParserState.Text;
        _buffer = string.Empty;
        return detected;
    }

    private IReadOnlyList<Dictionary<string, object?>> ExtractWebToolJsonCalls()
    {
        var detected = new List<Dictionary<string, object?>>();
        foreach (Match match in WebToolJsonRegex().Matches(_buffer))
        {
            try
            {
                var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(match.Groups["json"].Value);
                if (input is null)
                {
                    continue;
                }

                var tool = match.Groups["tool"].Value;
                if (tool == "WebFetch" && !input.ContainsKey("url"))
                {
                    continue;
                }

                if (tool == "WebSearch" && !input.ContainsKey("query"))
                {
                    continue;
                }

                detected.Add(CreateToolUse(($"toolu_heuristic_{Guid.NewGuid():N}")[..24], tool, input));
            }
            catch (JsonException)
            {
            }
        }

        if (detected.Count > 0)
        {
            _buffer = string.Empty;
        }

        return detected;
    }

    private static Dictionary<string, object?> CreateToolUse(string? id, string? name, Dictionary<string, object?> input)
    {
        if (name == "Task")
        {
            input["run_in_background"] = false;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = string.IsNullOrWhiteSpace(id) ? ($"toolu_heuristic_{Guid.NewGuid():N}")[..24] : id,
            ["name"] = name ?? "tool_call",
            ["input"] = input
        };
    }

    private static string StripControlTokens(string text)
    {
        return ControlTokenRegex().Replace(text, string.Empty);
    }

    private string SplitIncompleteControlTokenTail()
    {
        var start = _buffer.LastIndexOf("<|", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = _buffer.IndexOf("|>", start, StringComparison.Ordinal);
        if (end >= 0)
        {
            return string.Empty;
        }

        var prefix = _buffer[..start];
        _buffer = _buffer[start..];
        return prefix;
    }

    [GeneratedRegex("<\\|[^|>]{1,80}\\|>", RegexOptions.Compiled)]
    private static partial Regex ControlTokenRegex();

    [GeneratedRegex("\\u25cf\\s*<function=([^>]+)>", RegexOptions.Compiled)]
    private static partial Regex FunctionStartRegex();

    [GeneratedRegex("<parameter=([^>]+)>(.*?)(?:</parameter>|$)", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ParameterRegex();

    [GeneratedRegex("<parameter=([^>]+)>(.*)$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex PartialParameterRegex();

    [GeneratedRegex("\\b(?:use\\s+)?(?<tool>WebFetch|WebSearch)\\b.*?(?<json>\\{.*?\\})", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex WebToolJsonRegex();

    private enum ParserState
    {
        Text,
        MatchingFunction,
        ParsingParameters
    }
}
