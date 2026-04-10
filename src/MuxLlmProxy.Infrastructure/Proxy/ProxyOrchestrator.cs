using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;
using MuxLlmProxy.Infrastructure.Providers;

namespace MuxLlmProxy.Infrastructure.Proxy;

/// <summary>
/// Coordinates proxy target selection, upstream invocation, and failover.
/// </summary>
public sealed class ProxyOrchestrator : IProxyOrchestrator
{
    private readonly ITargetSelector _targetSelector;
    private readonly IProviderAdapterResolver _providerAdapterResolver;
    private readonly IAccountStore _accountStore;
    private readonly IRoundRobinState _roundRobinState;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProxyOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyOrchestrator"/> class.
    /// </summary>
    /// <param name="targetSelector">The target selector dependency.</param>
    /// <param name="providerAdapterResolver">The provider adapter resolver dependency.</param>
    /// <param name="accountStore">The account store dependency.</param>
    /// <param name="roundRobinState">The round-robin state dependency.</param>
    /// <param name="httpClientFactory">The HTTP client factory dependency.</param>
    /// <param name="logger">The logger dependency.</param>
    public ProxyOrchestrator(
        ITargetSelector targetSelector,
        IProviderAdapterResolver providerAdapterResolver,
        IAccountStore accountStore,
        IRoundRobinState roundRobinState,
        IHttpClientFactory httpClientFactory,
        ILogger<ProxyOrchestrator> logger)
    {
        _targetSelector = targetSelector;
        _providerAdapterResolver = providerAdapterResolver;
        _accountStore = accountStore;
        _roundRobinState = roundRobinState;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Executes the proxy workflow for a normalized request.
    /// </summary>
    /// <param name="request">The normalized proxy request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The translated proxy response.</returns>
    public async Task<ProxyResponse> ExecuteAsync(ProxyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targets = await _targetSelector.SelectAsync(request.Model, cancellationToken);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No eligible provider account was found for the requested model.");
        }

        var exhaustedRetryAt = GetEarliestCooldown(targets);
        Exception? lastUpstreamException = null;
        var client = _httpClientFactory.CreateClient("upstream");

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var adapter = _providerAdapterResolver.Resolve(target.ProviderType.Id);
            try
            {
                using var upstreamRequest = await adapter.PrepareRequestAsync(target, request, cancellationToken);
                await LogUpstreamRequestAsync(target, upstreamRequest, cancellationToken);

                var completionOption = request.Stream
                    ? HttpCompletionOption.ResponseHeadersRead
                    : HttpCompletionOption.ResponseContentRead;
                var upstreamResponse = await client.SendAsync(upstreamRequest, completionOption, cancellationToken);
                byte[]? body = null;
                var transferResponseOwnership = request.Stream && upstreamResponse.IsSuccessStatusCode;

                try
                {
                    if (!transferResponseOwnership || upstreamResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        body = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                        LogUpstreamResponse(target, upstreamResponse, body);
                    }
                    else
                    {
                        LogUpstreamResponseHeaders(target, upstreamResponse);
                    }

                    if (upstreamResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        if (!target.ProviderType.TracksAvailabilityWindows)
                        {
                            var translatedRateLimitResponse = await adapter.TranslateResponseAsync(target, request, upstreamResponse, body, cancellationToken);
                            if (translatedRateLimitResponse.WriteBodyAsync is null)
                            {
                                upstreamResponse.Dispose();
                            }

                            return translatedRateLimitResponse;
                        }

                        var retryAt = ResolveRetryAt(upstreamResponse, body ?? []);
                        exhaustedRetryAt ??= retryAt?.ToUnixTimeSeconds();
                        if (retryAt is not null)
                        {
                            await _accountStore.MarkRateLimitedAsync(target.Account.Id, retryAt.Value, cancellationToken);
                        }

                        upstreamResponse.Dispose();
                        continue;
                    }

                    if (target.ProviderType.TracksAvailabilityWindows)
                    {
                        await _accountStore.ClearRateLimitAsync(target.Account.Id, cancellationToken);
                    }
                    if (target.ProviderType.SupportsMulti)
                    {
                        var rotationKey = $"{request.Model}:{target.ProviderType.Id}";
                        var providerTargets = targets
                            .Where(targetItem => string.Equals(targetItem.ProviderType.Id, target.ProviderType.Id, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        var currentOffset = _roundRobinState.GetOffset(rotationKey, providerTargets.Length);
                        var providerLocalIndex = providerTargets
                            .TakeWhile(targetItem => !string.Equals(targetItem.Account.Id, target.Account.Id, StringComparison.OrdinalIgnoreCase))
                            .Count();
                        _roundRobinState.Advance(rotationKey, providerTargets.Length, currentOffset, providerLocalIndex);
                    }

                    var translatedResponse = await adapter.TranslateResponseAsync(target, request, upstreamResponse, body, cancellationToken);
                    if (translatedResponse.WriteBodyAsync is null)
                    {
                        upstreamResponse.Dispose();
                    }

                    return translatedResponse;
                }
                catch
                {
                    upstreamResponse.Dispose();
                    throw;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException)
            {
                lastUpstreamException = exception;
                _logger.LogWarning(exception, "Upstream request failed for provider {ProviderId} account {AccountId}", target.ProviderType.Id, target.Account.Id);
            }
        }

        if (lastUpstreamException is not null && exhaustedRetryAt is null)
        {
            throw new HttpRequestException("All matching providers failed to respond successfully.", lastUpstreamException);
        }

        var error = new ApiErrorEnvelope(new ApiError
        {
            Type = "rate_limit_error",
            Message = "All matching accounts are exhausted or temporarily unavailable.",
            NextAvailableAt = exhaustedRetryAt
        });

        return new ProxyResponse
        {
            StatusCode = (int)HttpStatusCode.TooManyRequests,
            Headers = ProviderHttpUtilities.CreateJsonHeaders(ProxyConstants.ContentTypes.Json),
            Body = JsonSerializer.SerializeToUtf8Bytes(error)
        };
    }

    /// <summary>
    /// Logs an upstream HTTP request.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="request">The upstream request message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when logging preparation finishes.</returns>
    private async Task LogUpstreamRequestAsync(ProxyTarget target, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation(
            "Upstream request {@UpstreamRequest}",
            new
            {
                Provider = target.ProviderType.Id,
                AccountId = target.Account.Id,
                Method = request.Method.Method,
                Url = request.RequestUri?.ToString(),
                Headers = HttpLoggingSanitizer.SanitizeHeaders(request.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))),
                ContentHeaders = request.Content is null
                    ? null
                    : HttpLoggingSanitizer.SanitizeHeaders(request.Content.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))),
                Body = HttpLoggingSanitizer.SanitizeBody(body)
            });
    }

    /// <summary>
    /// Logs an upstream HTTP response.
    /// </summary>
    /// <param name="target">The selected proxy target.</param>
    /// <param name="response">The upstream response message.</param>
    /// <param name="body">The upstream response body.</param>
    private void LogUpstreamResponse(ProxyTarget target, HttpResponseMessage response, byte[] body)
    {
        _logger.LogInformation(
            "Upstream response {@UpstreamResponse}",
            new
            {
                Provider = target.ProviderType.Id,
                AccountId = target.Account.Id,
                StatusCode = (int)response.StatusCode,
                Headers = HttpLoggingSanitizer.SanitizeHeaders(response.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))),
                ContentHeaders = HttpLoggingSanitizer.SanitizeHeaders(response.Content.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))),
                Body = HttpLoggingSanitizer.SanitizeBody(Encoding.UTF8.GetString(body))
            });
    }

    /// <summary>
    /// Logs upstream response metadata for streamed responses without buffering the body.
    /// </summary>
    private void LogUpstreamResponseHeaders(ProxyTarget target, HttpResponseMessage response)
    {
        _logger.LogInformation(
            "Upstream response headers {@UpstreamResponse}",
            new
            {
                Provider = target.ProviderType.Id,
                AccountId = target.Account.Id,
                StatusCode = (int)response.StatusCode,
                Headers = HttpLoggingSanitizer.SanitizeHeaders(response.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)))),
                ContentHeaders = HttpLoggingSanitizer.SanitizeHeaders(response.Content.Headers.Select(header => new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value))))
            });
    }

    /// <summary>
    /// Returns the earliest known cooldown timestamp from a sequence of accounts.
    /// </summary>
    /// <param name="targets">The targets to inspect.</param>
    /// <returns>The earliest known cooldown timestamp in Unix seconds, or <see langword="null"/>.</returns>
    private static long? GetEarliestCooldown(IEnumerable<ProxyTarget> targets)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? earliest = null;

        foreach (var target in targets)
        {
            if (!target.ProviderType.TracksAvailabilityWindows)
            {
                continue;
            }

            if (target.Account.AvailableAtRateLimit is long rateLimit && rateLimit > now)
            {
                earliest = earliest is null ? rateLimit : Math.Min(earliest.Value, rateLimit);
            }

            if (target.Account.AvailableAtWeeklyLimit is long weeklyLimit && weeklyLimit > now)
            {
                earliest = earliest is null ? weeklyLimit : Math.Min(earliest.Value, weeklyLimit);
            }
        }

        return earliest;
    }

    /// <summary>
    /// Resolves the retry timestamp from an upstream rate-limit response.
    /// </summary>
    /// <param name="response">The upstream response.</param>
    /// <param name="body">The upstream response body.</param>
    /// <returns>The retry timestamp when available; otherwise a fallback value.</returns>
    private static DateTimeOffset? ResolveRetryAt(HttpResponseMessage response, byte[] body)
    {
        if (response.Headers.TryGetValues(ProxyConstants.Headers.RetryAfter, out var retryAfterValues))
        {
            var retryAfter = retryAfterValues.FirstOrDefault();
            if (int.TryParse(retryAfter, out var seconds))
            {
                return DateTimeOffset.UtcNow.AddSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(retryAfter, out var parsed))
            {
                return parsed;
            }
        }

        foreach (var headerName in new[] { ProxyConstants.Headers.RateLimitReset, ProxyConstants.Headers.RateLimitResetRequests })
        {
            if (response.Headers.TryGetValues(headerName, out var values) && long.TryParse(values.FirstOrDefault(), out var unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
        }

        var text = Encoding.UTF8.GetString(body);
        foreach (var fragment in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = fragment.Trim('.', ',', ';', ':', '(', ')', '[', ']', '{', '}');
            if (long.TryParse(candidate, out var unixSeconds) && unixSeconds > 1_700_000_000)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
        }

        return DateTimeOffset.UtcNow.AddMinutes(ProxyConstants.Defaults.RetryAfterFallbackMinutes);
    }
}
