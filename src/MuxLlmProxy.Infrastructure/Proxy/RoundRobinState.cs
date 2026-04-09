using System.Collections.Concurrent;
using MuxLlmProxy.Core.Abstractions;

namespace MuxLlmProxy.Infrastructure.Proxy;

/// <summary>
/// Stores in-memory round-robin cursors keyed by logical routing key.
/// </summary>
public sealed class RoundRobinState : IRoundRobinState
{
    private readonly ConcurrentDictionary<string, int> _offsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the next rotation offset for a key.
    /// </summary>
    /// <param name="key">The routing key.</param>
    /// <param name="count">The candidate count.</param>
    /// <returns>The starting offset.</returns>
    public int GetOffset(string key, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return _offsets.TryGetValue(key, out var offset)
            ? Math.Abs(offset % count)
            : 0;
    }

    /// <summary>
    /// Advances the rotation cursor for a key.
    /// </summary>
    /// <param name="key">The routing key.</param>
    /// <param name="count">The candidate count.</param>
    /// <param name="currentOffset">The offset used to rotate the candidate list.</param>
    /// <param name="usedIndex">The provider-local index that succeeded inside the rotated list.</param>
    public void Advance(string key, int count, int currentOffset, int usedIndex)
    {
        if (count <= 0)
        {
            return;
        }

        _offsets[key] = (Math.Abs(currentOffset % count) + usedIndex + 1) % count;
    }
}
