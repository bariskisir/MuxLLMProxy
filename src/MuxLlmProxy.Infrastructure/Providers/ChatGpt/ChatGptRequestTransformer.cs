using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Infrastructure.Translation;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

public sealed class ChatGptRequestTransformer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex SystemReminderRegex = new("(?is)<system-reminder>.*?</system-reminder>", RegexOptions.Compiled);

    private readonly IMessageTranslator _messageTranslator;
    private readonly ChatGptModelCatalog _modelCatalog;

    public ChatGptRequestTransformer(IMessageTranslator messageTranslator, ChatGptModelCatalog modelCatalog)
    {
        _messageTranslator = messageTranslator;
        _modelCatalog = modelCatalog;
    }

    public async Task<byte[]> TransformAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        if (request.Format == ProxyFormat.OpenAiResponses)
        {
            return await TransformResponsesAsync(request, cancellationToken);
        }

        var openAiRequest = JsonSerializer.Deserialize<OpenAiChatRequest>(_messageTranslator.ToOpenAiRequest(request.Body), SerializerOptions)
            ?? throw new InvalidOperationException("The request payload could not be translated to an OpenAI-compatible format.");

        var normalized = OpenAiRequestNormalizer.Normalize(openAiRequest, request);
        var normalizedModel = await _modelCatalog.NormalizeModelAsync(normalized.Source.Model, cancellationToken);
        var planModeDirective = ExtractPlanModeDirective(normalized.ConversationMessages);
        if (string.IsNullOrWhiteSpace(planModeDirective))
        {
            planModeDirective = ExtractPlanModeDirectiveFromRawBody(request.Body);
        }

        var instructions = AppendDirective(ExtractInstructions(normalized.ConversationMessages), planModeDirective);
        var conversationMessages = PruneConversationMessages(RemoveInstructionMessages(normalized.ConversationMessages));

        var input = conversationMessages
            .SelectMany(ChatGptAdapter.MapMessageToChatGptInput)
            .ToArray();
        var prunedInput = PruneMappedInput(input);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = normalizedModel,
            ["input"] = prunedInput,
            ["stream"] = true,
            ["store"] = false,
            ["include"] = new[] { "reasoning.encrypted_content" }
        };

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            payload["instructions"] = instructions;
        }

        if (normalized.Source.TopP is not null)
        {
            payload["top_p"] = normalized.Source.TopP;
        }

        var normalizedTools = OpenAiRequestNormalizer.NormalizeTools(normalized.Source.Tools, request, conversationMessages);
        if (normalizedTools is not null && normalizedTools.Count > 0)
        {
            payload["tools"] = normalizedTools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = tool.Type,
                ["name"] = tool.Function.Name,
                ["description"] = tool.Function.Description,
                ["parameters"] = tool.Function.Parameters
            }).ToArray();
            payload["parallel_tool_calls"] = false;
        }

        if (normalized.Source.ToolChoice is not null)
        {
            payload["tool_choice"] = ChatGptAdapter.NormalizeToolChoice(normalized.Source.ToolChoice);
        }

        payload["text"] = new Dictionary<string, object?>
        {
            ["verbosity"] = !string.IsNullOrWhiteSpace(normalized.Source.Verbosity)
                ? normalized.Source.Verbosity
                : "medium"
        };

        payload["reasoning"] = new Dictionary<string, object?>
        {
            ["effort"] = await _modelCatalog.NormalizeReasoningEffortAsync(normalizedModel, normalized.Source.ReasoningEffort, cancellationToken),
            ["summary"] = ChatGptModelCatalog.NormalizeReasoningSummary(normalized.Source.ReasoningSummary)
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    private async Task<byte[]> TransformResponsesAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(request.Body)?.AsObject()
            ?? throw new InvalidOperationException("The responses request payload is invalid.");

        var normalizedModel = await _modelCatalog.NormalizeModelAsync(root["model"]?.GetValue<string>(), cancellationToken);

        root["model"] = normalizedModel;
        root["store"] = false;
        root["stream"] = true;
        root["max_output_tokens"] = null;
        root["max_completion_tokens"] = null;

        root["include"] = EnsureInclude(root["include"] as JsonArray);
        root["text"] = EnsureText(root["text"] as JsonObject);
        root["reasoning"] = await EnsureReasoningAsync(normalizedModel, root["reasoning"] as JsonObject, cancellationToken);

        if (root["input"] is JsonArray inputArray)
        {
            var filteredInput = FilterInput(inputArray);
            var extractedInstructions = ExtractInstructions(filteredInput);
            var planModeDirective = ExtractPlanModeDirective(filteredInput);
            if (string.IsNullOrWhiteSpace(planModeDirective))
            {
                planModeDirective = ExtractPlanModeDirectiveFromRawBody(request.Body);
            }

            if (!string.IsNullOrWhiteSpace(extractedInstructions))
            {
                root["instructions"] ??= extractedInstructions;
                filteredInput = RemoveInstructionItems(filteredInput);
            }

            root["instructions"] = AppendDirective(root["instructions"]?.GetValue<string>(), planModeDirective);

            filteredInput = NormalizeOrphanedToolOutputs(filteredInput);
            filteredInput = PruneInputMessages(filteredInput);
            root["input"] = filteredInput;
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions);
    }

    private static string? ExtractInstructions(IReadOnlyList<OpenAiMessage> messages)
    {
        var instructions = messages
            .Where(IsInstructionMessage)
            .Select(message => SanitizeInstructionText(OpenAiRequestNormalizer.FlattenMessageContent(message.Content)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return instructions.Length == 0
            ? null
            : string.Join("\n\n", instructions);
    }

    private static IReadOnlyList<OpenAiMessage> RemoveInstructionMessages(IReadOnlyList<OpenAiMessage> messages)
    {
        return messages
            .Where(message => !IsInstructionMessage(message))
            .ToArray();
    }

    private static bool IsInstructionMessage(OpenAiMessage message)
    {
        return string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray EnsureInclude(JsonArray? include)
    {
        var result = include is null ? [] : new JsonArray(include.Select(node => node?.DeepClone()).ToArray());
        var values = result
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        if (!values.Contains("reasoning.encrypted_content"))
        {
            result.Add("reasoning.encrypted_content");
        }

        return result;
    }

    private static JsonObject EnsureText(JsonObject? text)
    {
        var result = text is null ? new JsonObject() : JsonNode.Parse(text.ToJsonString())?.AsObject() ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(result["verbosity"]?.GetValue<string>()))
        {
            result["verbosity"] = "medium";
        }

        return result;
    }

    private async Task<JsonObject> EnsureReasoningAsync(string normalizedModel, JsonObject? reasoning, CancellationToken cancellationToken)
    {
        var result = reasoning is null ? new JsonObject() : JsonNode.Parse(reasoning.ToJsonString())?.AsObject() ?? new JsonObject();
        result["effort"] = await _modelCatalog.NormalizeReasoningEffortAsync(normalizedModel, result["effort"]?.GetValue<string>(), cancellationToken);
        result["summary"] = ChatGptModelCatalog.NormalizeReasoningSummary(result["summary"]?.GetValue<string>());
        return result;
    }

    private static JsonArray FilterInput(JsonArray input)
    {
        var result = new JsonArray();
        foreach (var node in input)
        {
            if (node is not JsonObject item)
            {
                result.Add(node?.DeepClone());
                continue;
            }

            var cloned = JsonNode.Parse(item.ToJsonString())?.AsObject() ?? new JsonObject();
            if (string.Equals(cloned["type"]?.GetValue<string>(), "item_reference", StringComparison.Ordinal))
            {
                continue;
            }

            cloned.Remove("id");
            result.Add(cloned);
        }

        return result;
    }

    private static string? ExtractInstructions(JsonArray input)
    {
        var instructions = input
            .OfType<JsonObject>()
            .Where(IsInstructionItem)
            .Select(item => SanitizeInstructionText(GetInputItemContentText(item)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return instructions.Length == 0
            ? null
            : string.Join("\n\n", instructions);
    }

    private static JsonArray RemoveInstructionItems(JsonArray input)
    {
        var result = new JsonArray();
        foreach (var node in input)
        {
            if (node is JsonObject item && IsInstructionItem(item))
            {
                continue;
            }

            result.Add(node?.DeepClone());
        }

        return result;
    }

    private static bool IsInstructionItem(JsonObject item)
    {
        var role = item["role"]?.GetValue<string>();
        return string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "system", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray NormalizeOrphanedToolOutputs(JsonArray input)
    {
        var functionCallIds = new HashSet<string>(StringComparer.Ordinal);
        var localShellCallIds = new HashSet<string>(StringComparer.Ordinal);
        var customToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in input.OfType<JsonObject>())
        {
            var type = node["type"]?.GetValue<string>();
            var callId = node["call_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(callId))
            {
                continue;
            }

            switch (type)
            {
                case "function_call":
                    functionCallIds.Add(callId);
                    break;
                case "local_shell_call":
                    localShellCallIds.Add(callId);
                    break;
                case "custom_tool_call":
                    customToolCallIds.Add(callId);
                    break;
            }
        }

        var result = new JsonArray();
        foreach (var node in input)
        {
            if (node is not JsonObject item)
            {
                result.Add(node?.DeepClone());
                continue;
            }

            var type = item["type"]?.GetValue<string>();
            var callId = item["call_id"]?.GetValue<string>();
            var isMatched = type switch
            {
                "function_call_output" => !string.IsNullOrWhiteSpace(callId) && (functionCallIds.Contains(callId) || localShellCallIds.Contains(callId)),
                "local_shell_call_output" => !string.IsNullOrWhiteSpace(callId) && localShellCallIds.Contains(callId),
                "custom_tool_call_output" => !string.IsNullOrWhiteSpace(callId) && customToolCallIds.Contains(callId),
                _ => true
            };

            if (isMatched || type is not ("function_call_output" or "local_shell_call_output" or "custom_tool_call_output"))
            {
                result.Add(item.DeepClone());
                continue;
            }

            var toolName = item["name"]?.GetValue<string>() ?? "tool";
            var outputText = item["output"]?.ToJsonString() ?? string.Empty;
            if (item["output"] is JsonValue outputValue && outputValue.TryGetValue<string>(out var outputString))
            {
                outputText = outputString;
            }

            if (outputText.Length > 16000)
            {
                outputText = outputText[..16000] + "\n...[truncated]";
            }

            result.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = $"[Previous {toolName} result; call_id={callId ?? "unknown"}]: {outputText}"
            });
        }

        return result;
    }

    private static IReadOnlyList<OpenAiMessage> PruneConversationMessages(IReadOnlyList<OpenAiMessage> messages)
    {
        var result = new List<OpenAiMessage>(messages.Count);

        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && message.ToolCalls is null)
            {
                var text = PromptTextSanitizer.Sanitize(OpenAiRequestNormalizer.FlattenMessageContent(message.Content)).Trim();
                if (ShouldDropTransientAssistantText(text))
                {
                    continue;
                }

                result.Add(message with { Content = text });
                continue;
            }

            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                var text = StripSystemReminders(OpenAiRequestNormalizer.FlattenMessageContent(message.Content));
                text = PromptTextSanitizer.Sanitize(text).Trim();
                result.Add(message with { Content = string.IsNullOrWhiteSpace(text) ? OpenAiRequestNormalizer.FlattenMessageContent(message.Content) : text });
                continue;
            }

            result.Add(message);
        }

        return DeduplicateConsecutiveAssistantMessages(result);
    }

    private static JsonArray PruneInputMessages(JsonArray input)
    {
        var result = new JsonArray();
        string? previousAssistantText = null;

        foreach (var node in input)
        {
            if (node is not JsonObject item)
            {
                result.Add(node?.DeepClone());
                continue;
            }

            if (!string.Equals(item["type"]?.GetValue<string>(), "message", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(item.DeepClone());
                continue;
            }

            var role = item["role"]?.GetValue<string>();
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(item.DeepClone());
                continue;
            }

            var text = PromptTextSanitizer.Sanitize(GetInputItemContentText(item)).Trim();
            if (ShouldDropTransientAssistantText(text)
                || string.Equals(previousAssistantText, text, StringComparison.Ordinal))
            {
                continue;
            }

            previousAssistantText = text;
            result.Add(CreateTextInputMessage("assistant", text, "output_text"));
        }

        return result;
    }

    private static JsonArray PruneMappedInput(IReadOnlyList<Dictionary<string, object?>> input)
    {
        var serialized = JsonSerializer.SerializeToNode(input, SerializerOptions) as JsonArray ?? [];
        return PruneInputMessages(serialized);
    }

    private static IReadOnlyList<OpenAiMessage> DeduplicateConsecutiveAssistantMessages(IReadOnlyList<OpenAiMessage> messages)
    {
        var result = new List<OpenAiMessage>(messages.Count);
        string? previousAssistantText = null;

        foreach (var message in messages)
        {
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                || message.ToolCalls is not null)
            {
                result.Add(message);
                previousAssistantText = null;
                continue;
            }

            var text = OpenAiRequestNormalizer.FlattenMessageContent(message.Content).Trim();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(previousAssistantText, text, StringComparison.Ordinal))
            {
                continue;
            }

            previousAssistantText = text;
            result.Add(message);
        }

        return result;
    }

    private static string SanitizeInstructionText(string text)
    {
        var planModeDirective = ExtractPlanModeDirective(text);
        var sanitized = PromptTextSanitizer.SanitizeInstructions(text);
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            return AppendDirective(sanitized.Trim(), planModeDirective);
        }

        var stripped = StripHarnessLines(PromptTextSanitizer.Sanitize(text));
        return AppendDirective(stripped, planModeDirective);
    }

    private static bool ShouldDropTransientAssistantText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (PromptTextSanitizer.ContainsClientHarness(text))
        {
            return true;
        }

        var normalized = text.Trim();
        return normalized.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("# Todos", StringComparison.Ordinal)
            || normalized.StartsWith("Preparing for", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Deciding on", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I need to", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I’m ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("I'm ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Statik dosyalari", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Mevcut HTML", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Mevcut HTML/CSS/JS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Plan mode", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("TaskCreate", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("AskUserQuestion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlanningToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName is "TaskCreate"
            or "TaskUpdate"
            or "TaskRead"
            or "EnterPlanMode"
            or "ExitPlanMode"
            or "Agent"
            or "AskUserQuestion"
            or "todowrite"
            or "todoread"
            or "task"
            or "question";
    }

    private static bool IsPlanningToolOutput(JsonNode? output)
    {
        var text = output switch
        {
            JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
            null => string.Empty,
            _ => output.ToJsonString()
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("Entered plan mode", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Task #", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Updated task", StringComparison.OrdinalIgnoreCase)
            || text.Contains("created successfully", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cannot create agent worktree", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Use AskUserQuestion", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPlanModeDirective(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal);
        if (normalized.Contains("Entered plan mode", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Plan mode is active", StringComparison.OrdinalIgnoreCase))
        {
            return "Plan mode is active. Do not edit files, run builds, or execute mutating commands. Only inspect the workspace, reason about approaches, and produce a plan until plan mode is exited.";
        }

        if (normalized.Contains("# Plan Mode - System Reminder", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Plan mode ACTIVE - you are in READ-ONLY phase", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("The user indicated that they do not want you to execute yet", StringComparison.OrdinalIgnoreCase))
        {
            return "Plan mode is active. This is a read-only planning phase. Do not edit files, create files, run builds, or execute mutating commands. Only inspect the workspace, analyze options, ask clarifying questions if needed, and produce a plan until plan mode is exited.";
        }

        return string.Empty;
    }

    private static string ExtractPlanModeDirective(IReadOnlyList<OpenAiMessage> messages)
    {
        foreach (var message in messages)
        {
            var directive = ExtractPlanModeDirective(OpenAiRequestNormalizer.FlattenMessageContent(message.Content));
            if (!string.IsNullOrWhiteSpace(directive))
            {
                return directive;
            }
        }

        return string.Empty;
    }

    private static string ExtractPlanModeDirective(JsonArray input)
    {
        foreach (var item in input.OfType<JsonObject>())
        {
            var type = item["type"]?.GetValue<string>();
            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                var outputDirective = ExtractPlanModeDirective(item["output"] switch
                {
                    JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
                    null => string.Empty,
                    _ => item["output"]?.ToJsonString() ?? string.Empty
                });

                if (!string.IsNullOrWhiteSpace(outputDirective))
                {
                    return outputDirective;
                }
            }

            var text = GetInputItemContentText(item);
            var textDirective = ExtractPlanModeDirective(text);
            if (!string.IsNullOrWhiteSpace(textDirective))
            {
                return textDirective;
            }
        }

        return string.Empty;
    }

    private static string ExtractPlanModeDirectiveFromRawBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        return ExtractPlanModeDirective(System.Text.Encoding.UTF8.GetString(body));
    }

    private static string AppendDirective(string? text, string directive)
    {
        if (string.IsNullOrWhiteSpace(directive))
        {
            return text ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return directive;
        }

        return $"{text}\n\n{directive}";
    }

    private static string StripHarnessLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !PromptTextSanitizer.ContainsClientHarness(line))
            .Where(line => !line.StartsWith("# Todos", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return lines.Length == 0 ? string.Empty : string.Join("\n", lines);
    }

    private static string StripSystemReminders(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return SystemReminderRegex.Replace(text, string.Empty).Trim();
    }

    private static JsonObject CreateTextInputMessage(string role, string text, string partType)
    {
        return new JsonObject
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = partType,
                ["text"] = text
            })
        };
    }

    private static string GetInputItemContentText(JsonObject item)
    {
        if (item["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var contentText))
        {
            return contentText;
        }

        if (item["content"] is JsonArray contentArray)
        {
            return string.Join(
                "\n",
                contentArray
                    .OfType<JsonObject>()
                    .Where(part => string.Equals(part["type"]?.GetValue<string>(), "input_text", StringComparison.Ordinal))
                    .Select(part => part["text"]?.GetValue<string>())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return string.Empty;
    }
}
