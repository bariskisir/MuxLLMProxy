using System.Text;
using System.Text.Json;

namespace MuxLlmProxy.Infrastructure.Translation;

internal sealed class AnthropicSseBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _messageId;
    private readonly string _model;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _thinking = new();
    private int _nextIndex;

    public AnthropicSseBuilder(string messageId, string model, int inputTokens)
    {
        _messageId = messageId;
        _model = model;
        InputTokens = inputTokens;
    }

    public int InputTokens { get; }

    public int ThinkingIndex { get; private set; } = -1;

    public int TextIndex { get; private set; } = -1;

    public bool ThinkingStarted { get; private set; }

    public bool TextStarted { get; private set; }

    public IDictionary<int, StreamingToolCallState> ToolStates { get; } = new Dictionary<int, StreamingToolCallState>();

    public string MessageStart()
    {
        return Format("message_start", new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = _messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = Array.Empty<object>(),
                ["model"] = _model,
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = InputTokens,
                    ["output_tokens"] = 1
                }
            }
        });
    }

    public string MessageDelta(string stopReason, int outputTokens)
    {
        return Format("message_delta", new Dictionary<string, object?>
        {
            ["type"] = "message_delta",
            ["delta"] = new Dictionary<string, object?>
            {
                ["stop_reason"] = stopReason,
                ["stop_sequence"] = null
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = InputTokens,
                ["output_tokens"] = outputTokens
            }
        });
    }

    public string MessageStop()
    {
        return Format("message_stop", new Dictionary<string, object?>
        {
            ["type"] = "message_stop"
        });
    }

    public IEnumerable<string> EnsureThinkingBlock()
    {
        if (TextStarted)
        {
            yield return StopTextBlock();
        }

        if (!ThinkingStarted)
        {
            yield return StartThinkingBlock();
        }
    }

    public IEnumerable<string> EnsureTextBlock()
    {
        if (ThinkingStarted)
        {
            yield return StopThinkingBlock();
        }

        if (!TextStarted)
        {
            yield return StartTextBlock();
        }
    }

    public IEnumerable<string> CloseContentBlocks()
    {
        if (ThinkingStarted)
        {
            yield return StopThinkingBlock();
        }

        if (TextStarted)
        {
            yield return StopTextBlock();
        }
    }

    public IEnumerable<string> CloseAllBlocks()
    {
        foreach (var item in CloseContentBlocks())
        {
            yield return item;
        }

        foreach (var state in ToolStates.Values.Where(state => state.Started).ToArray())
        {
            yield return ContentBlockStop(state.BlockIndex);
            state.Started = false;
        }
    }

    public string EmitThinkingDelta(string content)
    {
        _thinking.Append(content);
        return ContentBlockDelta(ThinkingIndex, "thinking_delta", "thinking", content);
    }

    public string EmitTextDelta(string content)
    {
        _text.Append(content);
        return ContentBlockDelta(TextIndex, "text_delta", "text", content);
    }

    public string StartToolBlock(int toolIndex, string toolId, string name)
    {
        var blockIndex = AllocateIndex();
        var state = EnsureToolState(toolIndex);
        state.BlockIndex = blockIndex;
        state.ToolId = toolId;
        state.Name = name;
        state.Started = true;
        return ContentBlockStart(blockIndex, "tool_use", new Dictionary<string, object?>
        {
            ["id"] = toolId,
            ["name"] = name,
            ["input"] = new Dictionary<string, object?>()
        });
    }

    public string EmitToolDelta(int toolIndex, string partialJson)
    {
        var state = EnsureToolState(toolIndex);
        state.Contents.Append(partialJson);
        return ContentBlockDelta(state.BlockIndex, "input_json_delta", "partial_json", partialJson);
    }

    public StreamingToolCallState EnsureToolState(int toolIndex)
    {
        if (!ToolStates.TryGetValue(toolIndex, out var state))
        {
            state = new StreamingToolCallState();
            ToolStates[toolIndex] = state;
        }

        return state;
    }

    public bool HasContentBlocks => TextIndex != -1 || ThinkingIndex != -1 || ToolStates.Values.Any(state => state.Started);

    public bool HasStartedTool => ToolStates.Values.Any(state => state.Started);

    public bool HasMeaningfulText => _text.ToString().Trim().Length > 0;

    public bool HasMeaningfulThinking => _thinking.ToString().Trim().Length > 0;

    public int EstimateOutputTokens()
    {
        var toolTokens = ToolStates.Values.Count(state => state.Started) * 50;
        return Math.Max(1, (_text.Length / 4) + (_thinking.Length / 4) + toolTokens);
    }

    private string StartThinkingBlock()
    {
        ThinkingIndex = AllocateIndex();
        ThinkingStarted = true;
        return ContentBlockStart(ThinkingIndex, "thinking", new Dictionary<string, object?>
        {
            ["thinking"] = string.Empty
        });
    }

    private string StopThinkingBlock()
    {
        ThinkingStarted = false;
        return ContentBlockStop(ThinkingIndex);
    }

    private string StartTextBlock()
    {
        TextIndex = AllocateIndex();
        TextStarted = true;
        return ContentBlockStart(TextIndex, "text", new Dictionary<string, object?>
        {
            ["text"] = string.Empty
        });
    }

    private string StopTextBlock()
    {
        TextStarted = false;
        return ContentBlockStop(TextIndex);
    }

    private int AllocateIndex()
    {
        return _nextIndex++;
    }

    private string ContentBlockStart(int index, string blockType, Dictionary<string, object?> properties)
    {
        var block = new Dictionary<string, object?>
        {
            ["type"] = blockType
        };
        foreach (var property in properties)
        {
            block[property.Key] = property.Value;
        }

        return Format("content_block_start", new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = block
        });
    }

    private string ContentBlockDelta(int index, string deltaType, string contentPropertyName, string content)
    {
        return Format("content_block_delta", new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta",
            ["index"] = index,
            ["delta"] = new Dictionary<string, object?>
            {
                ["type"] = deltaType,
                [contentPropertyName] = content
            }
        });
    }

    private string ContentBlockStop(int index)
    {
        return Format("content_block_stop", new Dictionary<string, object?>
        {
            ["type"] = "content_block_stop",
            ["index"] = index
        });
    }

    private static string Format(string eventType, Dictionary<string, object?> data)
    {
        return $"event: {eventType}\ndata: {JsonSerializer.Serialize(data, SerializerOptions)}\n\n";
    }
}

internal sealed class StreamingToolCallState
{
    public int BlockIndex { get; set; } = -1;

    public string ToolId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Started { get; set; }

    public StringBuilder Contents { get; } = new();

    public StringBuilder TaskArgumentBuffer { get; } = new();

    public bool TaskArgumentsEmitted { get; set; }

    public string PreStartArguments { get; set; } = string.Empty;
}
