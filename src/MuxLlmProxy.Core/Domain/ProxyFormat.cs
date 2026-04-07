namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Identifies the inbound API format accepted by the proxy.
/// </summary>
public enum ProxyFormat
{
    /// <summary>
    /// OpenAI chat completions compatible format.
    /// </summary>
    OpenAi = 0,

    /// <summary>
    /// Anthropic messages compatible format.
    /// </summary>
    Anthropic = 1
}
