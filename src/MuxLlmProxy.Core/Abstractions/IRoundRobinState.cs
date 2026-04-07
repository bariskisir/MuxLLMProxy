namespace MuxLlmProxy.Core.Abstractions;

/// <summary>
/// Defines an in-memory round-robin cursor store.
/// </summary>
public interface IRoundRobinState
{
    /// <summary>
    /// Gets the next rotation offset for a key.
    /// </summary>
    /// <param name="key">The logical routing key.</param>
    /// <param name="count">The candidate count.</param>
    /// <returns>The starting offset.</returns>
    int GetOffset(string key, int count);

    /// <summary>
    /// Advances the rotation cursor for a key.
    /// </summary>
    /// <param name="key">The logical routing key.</param>
    /// <param name="count">The candidate count.</param>
    /// <param name="usedIndex">The index that succeeded.</param>
    void Advance(string key, int count, int usedIndex);
}
