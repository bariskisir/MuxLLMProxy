namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines translation between Anthropic and upstream provider payloads.
/// </summary>
public interface IMessageTranslator
{
    /// <summary>
    /// Converts an Anthropic messages request into an OpenAI-compatible chat request payload.
    /// </summary>
    /// <param name="anthropicJson">The original Anthropic request JSON.</param>
    /// <returns>The translated OpenAI-compatible JSON payload.</returns>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the input payload is invalid.</exception>
    byte[] ToOpenAiRequest(byte[] anthropicJson);

    /// <summary>
    /// Converts an OpenAI-compatible non-streaming response into an Anthropic response payload.
    /// </summary>
    /// <param name="openAiJson">The upstream response JSON.</param>
    /// <param name="model">The requested model identifier.</param>
    /// <returns>The translated Anthropic JSON payload.</returns>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the input payload is invalid.</exception>
    byte[] ToAnthropicResponse(byte[] openAiJson, string model);

    /// <summary>
    /// Converts an OpenAI-compatible SSE stream payload into Anthropic SSE events.
    /// </summary>
    /// <param name="openAiStreamBytes">The buffered upstream stream content.</param>
    /// <param name="model">The requested model identifier.</param>
    /// <returns>The translated Anthropic stream payload.</returns>
    byte[] ToAnthropicStream(byte[] openAiStreamBytes, string model);
}
