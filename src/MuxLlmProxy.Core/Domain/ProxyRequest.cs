namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a normalized client request passed into the orchestration layer.
/// </summary>
public sealed record ProxyRequest
{
    /// <summary>
    /// Gets the requested model identifier.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets a value indicating whether streaming is requested.
    /// </summary>
    public required bool Stream { get; init; }

    /// <summary>
    /// Gets the inbound API format identifier.
    /// </summary>
    public required ProxyFormat Format { get; init; }

    /// <summary>
    /// Gets the original request body bytes.
    /// </summary>
    public required byte[] Body { get; init; }

    /// <summary>
    /// Gets the optional client session identifier.
    /// </summary>
    public string? SessionId { get; init; }
}
