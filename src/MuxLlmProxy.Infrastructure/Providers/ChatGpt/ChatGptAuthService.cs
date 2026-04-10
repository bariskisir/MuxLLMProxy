using System.Net.Http.Headers;
using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

/// <summary>
/// Manages OAuth token lifecycle for ChatGPT provider accounts.
/// </summary>
public sealed class ChatGptAuthService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccountStore _accountStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGptAuthService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory dependency.</param>
    /// <param name="accountStore">The account store dependency.</param>
    public ChatGptAuthService(IHttpClientFactory httpClientFactory, IAccountStore accountStore)
    {
        _httpClientFactory = httpClientFactory;
        _accountStore = accountStore;
    }

    /// <summary>
    /// Returns a valid access token and the associated account identifier for the supplied account.
    /// Refreshes the token transparently when it is expired or about to expire.
    /// </summary>
    /// <param name="account">The account to authenticate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the access token and optional account identifier.</returns>
    public async Task<(string AccessToken, string? AccountId)> GetAccessContextAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var accessToken = await EnsureValidAccessTokenAsync(account, cancellationToken);
        return (accessToken, ExtractChatGptAccountId(accessToken));
    }

    /// <summary>
    /// Ensures the access token is valid, refreshing it if necessary.
    /// </summary>
    /// <param name="account">The account to validate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A valid access token string.</returns>
    private async Task<string> EnsureValidAccessTokenAsync(Account account, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.Access) && !IsExpiring(account.Expire))
        {
            return account.Access;
        }

        if (string.IsNullOrWhiteSpace(account.Refresh))
        {
            throw new InvalidOperationException($"ChatGPT account '{account.Id}' is missing its access token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ProxyConstants.Cli.ChatGptTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.Refresh,
                ["client_id"] = ProxyConstants.Cli.ChatGptClientId
            })
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProxyConstants.ContentTypes.FormUrlEncoded);

        using var response = await _httpClientFactory.CreateClient("upstream").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ChatGPT token refresh failed for account '{account.Id}'.");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            || !document.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement)
            || !document.RootElement.TryGetProperty("expires_in", out var expiresInElement))
        {
            throw new InvalidOperationException(ProxyConstants.Messages.InvalidOAuthTokenResponse);
        }

        var accessToken = accessTokenElement.GetString() ?? throw new InvalidOperationException(ProxyConstants.Messages.MissingAccessToken);
        var refreshToken = refreshTokenElement.GetString() ?? throw new InvalidOperationException(ProxyConstants.Messages.MissingRefreshToken);
        var expire = DateTimeOffset.UtcNow.AddSeconds(expiresInElement.GetInt64()).ToUnixTimeMilliseconds();

        await _accountStore.UpdateAuthenticationAsync(account.Id, accessToken, refreshToken, expire, cancellationToken);
        return accessToken;
    }

    /// <summary>
    /// Determines whether the token is expired or will expire within the safety buffer window.
    /// </summary>
    /// <param name="expireUnixMilliseconds">The expiration timestamp in Unix milliseconds.</param>
    /// <returns><see langword="true"/> if the token is expiring; otherwise <see langword="false"/>.</returns>
    private static bool IsExpiring(long? expireUnixMilliseconds)
    {
        if (expireUnixMilliseconds is null)
        {
            return true;
        }

        return expireUnixMilliseconds.Value <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Extracts the specific ChatGPT account identifier from a JWT access token claim.
    /// </summary>
    /// <param name="accessToken">The JWT access token.</param>
    /// <returns>The account identifier if found; otherwise <see langword="null"/>.</returns>
    private static string? ExtractChatGptAccountId(string? accessToken)
    {
        return JwtUtilities.TryReadStringClaim(accessToken, "https://api.openai.com/auth", "chatgpt_account_id");
    }
}
