using System.Text.Json.Serialization;

namespace MuxLlmProxy.Core.Contracts;

/// <summary>
/// Represents an OpenAI chat completions request.
/// </summary>
public sealed record OpenAiChatRequest
{
    /// <summary>
    /// Gets the requested model identifier.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the upstream messages.
    /// </summary>
    public required IReadOnlyList<OpenAiMessage> Messages { get; init; }

    /// <summary>
    /// Gets the optional max tokens value.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Gets the optional temperature.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Gets the optional top-p value.
    /// </summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    /// <summary>
    /// Gets a value indicating whether upstream streaming is requested.
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>
    /// Gets the optional upstream tools.
    /// </summary>
    public IReadOnlyList<OpenAiTool>? Tools { get; init; }

    /// <summary>
    /// Gets the optional tool choice.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; init; }

    /// <summary>
    /// Gets the optional reasoning summary mode.
    /// </summary>
    [JsonPropertyName("reasoningSummary")]
    public string? ReasoningSummary { get; init; }

    /// <summary>
    /// Gets the optional reasoning effort.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Gets the optional verbosity mode.
    /// </summary>
    public string? Verbosity { get; init; }
}

/// <summary>
/// Represents an OpenAI chat message.
/// </summary>
public sealed record OpenAiMessage
{
    /// <summary>
    /// Gets the message role.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Gets the message content.
    /// </summary>
    public object? Content { get; init; }

    /// <summary>
    /// Gets the optional tool call identifier for tool messages.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Gets the optional assistant tool calls.
    /// </summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<object>? ToolCalls { get; init; }
}

/// <summary>
/// Represents an OpenAI tool definition.
/// </summary>
public sealed record OpenAiTool
{
    /// <summary>
    /// Gets the tool type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the function descriptor.
    /// </summary>
    public required OpenAiFunctionDefinition Function { get; init; }
}

/// <summary>
/// Represents an OpenAI function descriptor.
/// </summary>
public sealed record OpenAiFunctionDefinition
{
    /// <summary>
    /// Gets the function name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the optional function description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the optional input schema.
    /// </summary>
    public IDictionary<string, object?>? Parameters { get; init; }
}
