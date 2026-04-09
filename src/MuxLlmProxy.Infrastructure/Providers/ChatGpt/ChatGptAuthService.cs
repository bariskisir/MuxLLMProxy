using System.Net.Http.Headers;
using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;

namespace MuxLlmProxy.Infrastructure.Providers.ChatGpt;

public sealed class ChatGptAuthService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccountStore _accountStore;

    public ChatGptAuthService(IHttpClientFactory httpClientFactory, IAccountStore accountStore)
    {
        _httpClientFactory = httpClientFactory;
        _accountStore = accountStore;
    }

    public async Task<(string AccessToken, string? AccountId)> GetAccessContextAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var accessToken = await EnsureValidAccessTokenAsync(account, cancellationToken);
        return (accessToken, ExtractChatGptAccountId(accessToken));
    }

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

    private static bool IsExpiring(long? expireUnixMilliseconds)
    {
        if (expireUnixMilliseconds is null)
        {
            return true;
        }

        return expireUnixMilliseconds.Value <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
    }

    private static string? ExtractChatGptAccountId(string? accessToken)
    {
        return JwtUtilities.TryReadStringClaim(accessToken, "https://api.openai.com/auth", "chatgpt_account_id");
    }
}
