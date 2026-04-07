namespace MuxLlmProxy.Core.Domain;

/// <summary>
/// Represents a provider limit snapshot returned to clients.
/// </summary>
/// <param name="Label">The label describing the limit window.</param>
/// <param name="LeftPercent">The estimated remaining percentage.</param>
/// <param name="UsedPercent">The estimated used percentage.</param>
/// <param name="ResetsAt">The Unix timestamp when the limit resets.</param>
/// <param name="WindowDurationMins">The window duration in minutes.</param>
public sealed record ProviderLimitSnapshot(
    string Label,
    int LeftPercent,
    int UsedPercent,
    long ResetsAt,
    int WindowDurationMins);
