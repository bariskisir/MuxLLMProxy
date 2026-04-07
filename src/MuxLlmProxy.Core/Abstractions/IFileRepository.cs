namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines file-system operations used by the persistence layer.
/// </summary>
public interface IFileRepository
{
    /// <summary>
    /// Reads and deserializes a JSON file.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="path">The file path to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized payload.</returns>
    /// <exception cref="System.IO.IOException">Thrown when the file cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the file contains invalid JSON.</exception>
    Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a JSON payload to disk atomically.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="path">The file path to write.</param>
    /// <param name="value">The payload value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the write operation finishes.</returns>
    /// <exception cref="System.IO.IOException">Thrown when the file cannot be written.</exception>
    Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken);

    /// <summary>
    /// Determines whether a file exists.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns><see langword="true"/> when the file exists; otherwise <see langword="false"/>.</returns>
    bool Exists(string path);
}
