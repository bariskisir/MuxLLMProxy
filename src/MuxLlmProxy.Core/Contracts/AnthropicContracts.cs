using System.Text.Json.Serialization;

namespace MuxLlmProxy.Core.Contracts;

/// <summary>
/// Represents an Anthropic messages request.
/// </summary>
public sealed record AnthropicMessagesRequest
{
    /// <summary>
    /// Gets the requested model identifier.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the optional system prompt payload.
    /// </summary>
    public object? System { get; init; }

    /// <summary>
    /// Gets the conversation messages.
    /// </summary>
    public required IReadOnlyList<AnthropicMessage> Messages { get; init; }

    /// <summary>
    /// Gets the maximum token count.
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
    /// Gets a value indicating whether streaming is requested.
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>
    /// Gets the optional tool definitions.
    /// </summary>
    public IReadOnlyList<AnthropicTool>? Tools { get; init; }

    /// <summary>
    /// Gets the optional tool choice directive.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; init; }

    /// <summary>
    /// Gets the optional thinking configuration.
    /// </summary>
    public AnthropicThinkingConfig? Thinking { get; init; }
}

/// <summary>
/// Represents Anthropic thinking configuration.
/// </summary>
public sealed record AnthropicThinkingConfig
{
    /// <summary>
    /// Gets the thinking mode.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets the optional thinking budget token count.
    /// </summary>
    [JsonPropertyName("budget_tokens")]
    public int? BudgetTokens { get; init; }
}

/// <summary>
/// Represents an Anthropic message.
/// </summary>
/// <param name="Role">The message role.</param>
/// <param name="Content">The message content.</param>
public sealed record AnthropicMessage(string Role, object? Content);

/// <summary>
/// Represents an Anthropic tool definition.
/// </summary>
public sealed record AnthropicTool
{
    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the optional Anthropic tool type identifier.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets the optional nested function descriptor when the tool is sent in OpenAI format.
    /// </summary>
    public AnthropicToolFunction? Function { get; init; }

    /// <summary>
    /// Gets the optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the JSON schema describing the tool input.
    /// </summary>
    [JsonPropertyName("input_schema")]
    public IDictionary<string, object?>? InputSchema { get; init; }
}

/// <summary>
/// Represents a nested tool function descriptor.
/// </summary>
public sealed record AnthropicToolFunction
{
    /// <summary>
    /// Gets the function name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the optional function description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the optional parameters schema.
    /// </summary>
    public IDictionary<string, object?>? Parameters { get; init; }
}
