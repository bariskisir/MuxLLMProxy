namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a translated HTTP response returned by the proxy pipeline.
/// </summary>
public sealed record ProxyResponse
{
    /// <summary>
    /// Gets the HTTP status code.
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Gets the headers to write to the response.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>
    /// Gets the response body bytes.
    /// </summary>
    public required byte[] Body { get; init; }
}
