using System.Text.Json.Serialization;
using MuxLlmProxy.Core.Domain;

namespace MuxLlmProxy.Core.Contracts;

/// <summary>
/// Represents a models response payload.
/// </summary>
/// <param name="Data">The exposed models.</param>
public sealed record ModelsResponse(IReadOnlyList<ProviderModel> Data);

/// <summary>
/// Represents a limits response payload.
/// </summary>
/// <param name="Data">The exposed limit entries.</param>
public sealed record LimitsResponse(IReadOnlyList<LimitEntry> Data);

/// <summary>
/// Represents a limit entry returned by the API.
/// </summary>
public sealed record LimitEntry
{
    /// <summary>
    /// Gets the provider type identifier.
    /// </summary>
    [JsonPropertyName("type_id")]
    public required string TypeId { get; init; }

    /// <summary>
    /// Gets the account identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets a value indicating whether the entry has limit data.
    /// </summary>
    [JsonPropertyName("has_limits")]
    public required bool HasLimits { get; init; }

    /// <summary>
    /// Gets the optional limit snapshot.
    /// </summary>
    public ProviderLimitSnapshot? Limit { get; init; }

    /// <summary>
    /// Gets the optional account token value.
    /// </summary>
    public string? Token { get; internal set; }
}

/// <summary>
/// Represents a normalized API error response.
/// </summary>
/// <param name="Error">The error details.</param>
public sealed record ApiErrorEnvelope(ApiError Error);

/// <summary>
/// Represents normalized API error details.
/// </summary>
public sealed record ApiError
{
    /// <summary>
    /// Gets the error type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the optional next-available timestamp in Unix seconds.
    /// </summary>
    [JsonPropertyName("next_available_at")]
    public long? NextAvailableAt { get; init; }
}
