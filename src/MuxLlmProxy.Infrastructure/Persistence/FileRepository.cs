using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;

namespace MuxLlmProxy.Infrastructure.Persistence;

/// <summary>
/// Provides JSON file-system access for the persistence layer.
/// </summary>
public sealed class FileRepository : IFileRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads and deserializes a JSON file.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="path">The file path to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized payload.</returns>
    public async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value ?? throw new InvalidOperationException($"The file '{path}' did not contain a valid JSON payload.");
    }

    /// <summary>
    /// Writes a JSON payload to disk atomically.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="path">The file path to write.</param>
    /// <param name="value">The payload value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the write finishes.</returns>
    public async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The target path does not have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Determines whether a file exists.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns><see langword="true"/> when the file exists; otherwise <see langword="false"/>.</returns>
    public bool Exists(string path)
    {
        return File.Exists(path);
    }
}
