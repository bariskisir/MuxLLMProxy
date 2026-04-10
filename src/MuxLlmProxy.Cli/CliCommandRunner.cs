using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuxLlmProxy.Core.Abstractions;
using MuxLlmProxy.Core.Configuration;
using MuxLlmProxy.Core.Contracts;
using MuxLlmProxy.Core.Domain;
using MuxLlmProxy.Core.Utilities;

namespace MuxLlmProxy.Cli;

/// <summary>
/// Executes interactive CLI commands for account management and limit display.
/// </summary>
public sealed class CliCommandRunner
{
    private static readonly string[] ProviderOrder = [ProxyConstants.Providers.ChatGpt, ProxyConstants.Providers.OpenCode, ProxyConstants.Providers.OpenRouter];
    private readonly IAccountStore _accountStore;
    private readonly IProviderCatalog _providerCatalog;
    private readonly ILimitsQueryService _limitsQueryService;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliCommandRunner"/> class.
    /// </summary>
    /// <param name="accountStore">The account store dependency.</param>
    /// <param name="providerCatalog">The provider catalog dependency.</param>
    /// <param name="limitsQueryService">The limits query dependency.</param>
    /// <param name="httpClientFactory">The HTTP client factory dependency.</param>
    public CliCommandRunner(
        IAccountStore accountStore,
        IProviderCatalog providerCatalog,
        ILimitsQueryService limitsQueryService,
        IHttpClientFactory httpClientFactory)
    {
        _accountStore = accountStore;
        _providerCatalog = providerCatalog;
        _limitsQueryService = limitsQueryService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Executes the requested CLI command when supported.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when a CLI command was handled; otherwise <see langword="false"/>.</returns>
    public async Task<bool> TryRunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(ProxyConstants.Messages.CliCommandRequired);
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        switch (command)
        {
            case ProxyConstants.Cli.AddCommand:
                await RunAddAsync(cancellationToken);
                return true;
            case ProxyConstants.Cli.LimitsCommand:
                await RunLimitsAsync(cancellationToken);
                return true;
            default:
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, ProxyConstants.Messages.UnsupportedCliCommandFormat, command));
                return false;
        }
    }

    /// <summary>
    /// Processes the 'add' command to register a new provider account.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task RunAddAsync(CancellationToken cancellationToken)
    {
        var providers = (await _providerCatalog.GetAsync(cancellationToken))
            .Where(provider => ProviderOrder.Contains(provider.Id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(provider => Array.IndexOf(ProviderOrder, provider.Id))
            .ToArray();

        if (providers.Length == 0)
        {
            throw new InvalidOperationException(ProxyConstants.Messages.NoProvidersAvailable);
        }

        Console.WriteLine(ProxyConstants.Messages.SelectProvider);
        for (var index = 0; index < providers.Length; index++)
        {
            Console.WriteLine($"{index + 1}. {ToDisplayName(providers[index].Id)}");
        }

        var selectedIndex = ReadProviderSelection(providers.Length) - 1;
        var selectedProvider = providers[selectedIndex];
        var account = selectedProvider.Id switch
        {
            ProxyConstants.Providers.ChatGpt => await CreateChatGptAccountAsync(cancellationToken),
            ProxyConstants.Providers.OpenCode => CreateOpenCodeAccount(),
            ProxyConstants.Providers.OpenRouter => CreateOpenRouterAccount(),
            _ => throw new InvalidOperationException($"Unsupported provider '{selectedProvider.Id}'.")
        };

        await _accountStore.SaveAsync(account, !selectedProvider.TracksAvailabilityWindows, cancellationToken);
        Console.WriteLine($"Saved {ToDisplayName(selectedProvider.Id)} account: {account.Id}");
    }

    /// <summary>
    /// Processes the 'limits' command to display account usage and quotas.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task RunLimitsAsync(CancellationToken cancellationToken)
    {
        var entries = await _limitsQueryService.GetLimitsAsync(cancellationToken);
        var limitedEntries = entries
            .Where(entry => entry.HasLimits && entry.Limit is not null)
            .ToArray();

        if (limitedEntries.Length == 0)
        {
            Console.WriteLine(ProxyConstants.Messages.NoAccountsConfigured);
            return;
        }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, ProxyConstants.Messages.LoadedAccountLimitsFormat, limitedEntries.Length));
        Console.WriteLine();

        var providerGroups = limitedEntries
            .GroupBy(entry => entry.TypeId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Array.IndexOf(ProviderOrder, group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProviderLimitGroup(group.Key, group.ToArray()))
            .ToArray();

        var aggregateGroups = providerGroups
            .Where(group => group.Entries.Length > 1)
            .ToArray();

        if (aggregateGroups.Length > 0)
        {
            Console.WriteLine("Provider totals");
            Console.WriteLine();

            foreach (var providerGroup in aggregateGroups)
            {
                RenderProviderAggregate(providerGroup);
            }

            Console.WriteLine();
        }

        foreach (var providerGroup in providerGroups)
        {
            RenderProviderLimits(providerGroup);
        }
    }

    /// <summary>
    /// Reads a numeric provider selection from the console.
    /// </summary>
    /// <param name="maxOption">The maximum valid option number.</param>
    /// <returns>The selected option index (1-based).</returns>
    private static int ReadProviderSelection(int maxOption)
    {
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out var value) && value >= 1 && value <= maxOption)
            {
                return value;
            }

            Console.WriteLine(ProxyConstants.Messages.InvalidSelection);
        }
    }

    /// <summary>
    /// Orchestrates the interactive OAuth flow for a new ChatGPT account.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The newly created account.</returns>
    private async Task<Account> CreateChatGptAccountAsync(CancellationToken cancellationToken)
    {
        var verifier = CreateCodeVerifier();
        var state = CreateState();
        var authorizationUrl = BuildAuthorizationUrl(state, verifier);

        Console.WriteLine();
        Console.WriteLine(ProxyConstants.Messages.OpenLoginUrl);
        Console.WriteLine(authorizationUrl);
        Console.WriteLine();

        var pastedValue = ReadRequiredValue(ProxyConstants.Messages.PasteCallbackUrlPrompt);
        var parsedInput = ParseAuthorizationInput(pastedValue);
        if (string.IsNullOrWhiteSpace(parsedInput.Code))
        {
            throw new InvalidOperationException(ProxyConstants.Messages.MissingAuthorizationCode);
        }

        if (!string.IsNullOrWhiteSpace(parsedInput.State)
            && !string.Equals(parsedInput.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ProxyConstants.Messages.OAuthStateMismatch);
        }

        var tokenResult = await ExchangeAuthorizationCodeAsync(parsedInput.Code, verifier, cancellationToken);
        var email = TryExtractEmailFromJwt(tokenResult.Access);
        var fallbackId = $"{ProxyConstants.Providers.ChatGpt}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}";
        var suggestedId = !string.IsNullOrWhiteSpace(email) ? $"{ProxyConstants.Providers.ChatGpt}_{email}" : fallbackId;

        return new Account
        {
            Id = suggestedId,
            ProviderType = ProxyConstants.Providers.ChatGpt,
            Access = tokenResult.Access,
            Refresh = tokenResult.Refresh,
            Expire = tokenResult.ExpiresAt,
            Enabled = true
        };
    }

    /// <summary>
    /// Creates a placeholder account for the OpenCode provider.
    /// </summary>
    /// <returns>The OpenCode account.</returns>
    private static Account CreateOpenCodeAccount()
    {
        return new Account
        {
            Id = $"{ProxyConstants.Providers.OpenCode}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}",
            ProviderType = ProxyConstants.Providers.OpenCode,
            Token = ProxyConstants.Cli.PublicToken,
            Enabled = true
        };
    }

    /// <summary>
    /// Prompts for an API token and creates an OpenRouter account.
    /// </summary>
    /// <returns>The OpenRouter account.</returns>
    private static Account CreateOpenRouterAccount()
    {
        var token = ReadRequiredValue(ProxyConstants.Messages.OpenRouterTokenPrompt);
        return new Account
        {
            Id = $"{ProxyConstants.Providers.OpenRouter}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}",
            ProviderType = ProxyConstants.Providers.OpenRouter,
            Token = token,
            Enabled = true
        };
    }

    /// <summary>
    /// Prompts the user for a required console input value.
    /// </summary>
    /// <param name="prompt">The prompt message.</param>
    /// <returns>The non-empty input value.</returns>
    private static string ReadRequiredValue(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            Console.WriteLine(ProxyConstants.Messages.ValueRequired);
        }
    }

    /// <summary>
    /// Constructs the OAuth authorization URL for ChatGPT.
    /// </summary>
    /// <param name="state">The OAuth state parameter.</param>
    /// <param name="verifier">The PKCE code verifier.</param>
    /// <returns>The encoded authorization URL.</returns>
    private static string BuildAuthorizationUrl(string state, string verifier)
    {
        var url = new UriBuilder(ProxyConstants.Cli.ChatGptAuthorizeUrl);
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ProxyConstants.Cli.ChatGptClientId,
            ["redirect_uri"] = ProxyConstants.Cli.ChatGptRedirectUri,
            ["scope"] = ProxyConstants.Cli.ChatGptScope,
            ["code_challenge"] = CreateCodeChallenge(verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = ProxyConstants.ProviderHeaders.CodexOriginator
        };
        url.Query = string.Join("&", query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
        return url.ToString();
    }

    /// <summary>
    /// Returns the human-readable label for a provider identifier.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The display name.</returns>
    private static string ToDisplayName(string providerId)
    {
        return providerId switch
        {
            ProxyConstants.Providers.ChatGpt => ProxyConstants.Labels.ChatGpt,
            ProxyConstants.Providers.OpenCode => ProxyConstants.Labels.OpenCode,
            ProxyConstants.Providers.OpenRouter => ProxyConstants.Labels.OpenRouter,
            _ => providerId
        };
    }

    /// <summary>
    /// Trims the provider prefix from an account identifier for display.
    /// </summary>
    /// <param name="entry">The limit entry containing the ID.</param>
    /// <returns>The normalized account name.</returns>
    private static string NormalizeAccountName(LimitEntry entry)
    {
        var prefix = $"{entry.TypeId}_";
        return entry.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? entry.Id[prefix.Length..]
            : entry.Id;
    }

    /// <summary>
    /// Formats the usage bar or status text for an account.
    /// </summary>
    /// <param name="entry">The limit entry to format.</param>
    /// <returns>The usage string.</returns>
    private static string FormatUsage(LimitEntry entry)
    {
        if (!entry.HasLimits)
        {
            return ProxyConstants.Labels.Unavailable;
        }

        if (entry.Limit is null)
        {
            return ProxyConstants.Labels.Unknown;
        }

        return $"[{BuildBar(entry.Limit.LeftPercent)}]";
    }

    /// <summary>
    /// Renders an aggregate usage bar for all accounts of a provider.
    /// </summary>
    /// <param name="group">The provider limit group.</param>
    private static void RenderProviderAggregate(ProviderLimitGroup group)
    {
        var totalCapacity = group.Entries.Length * 100;
        var totalRemaining = group.Entries.Sum(entry => Math.Clamp(entry.Limit?.LeftPercent ?? 0, 0, 100));

        Console.WriteLine($"{ToDisplayName(group.ProviderId)}: [{BuildAggregateBar(totalRemaining, totalCapacity)}] {totalRemaining}/{totalCapacity}");
    }

    /// <summary>
    /// Renders a detailed table of account limits for a provider.
    /// </summary>
    /// <param name="group">The provider limit group.</param>
    private static void RenderProviderLimits(ProviderLimitGroup group)
    {
        var sortedEntries = group.Entries
            .OrderByDescending(entry => entry.Limit?.LeftPercent ?? -1)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine(ToDisplayName(group.ProviderId));
        Console.WriteLine();

        var rows = sortedEntries
            .Select(entry => new[]
            {
                NormalizeAccountName(entry),
                FormatUsage(entry),
                $"{entry.Limit!.LeftPercent}%",
                FormatReset(entry.Limit.ResetsAt)
            })
            .ToArray();

        RenderTable(
            null,
            [ProxyConstants.Labels.Account, ProxyConstants.Labels.WeeklyLimit, ProxyConstants.Labels.Left, ProxyConstants.Labels.Resets],
            rows);

        Console.WriteLine();
    }

    /// <summary>
    /// Builds a single account progress bar.
    /// </summary>
    /// <param name="leftPercent">The remaining percentage (0-100).</param>
    /// <returns>The ASCII progress bar.</returns>
    private static string BuildBar(int leftPercent)
    {
        const int barLength = ProxyConstants.Defaults.ProgressBarWidth;
        var clamped = Math.Clamp(leftPercent, 0, 100);
        var filledCount = (int)Math.Round(clamped / 100d * barLength, MidpointRounding.AwayFromZero);
        return new string('#', filledCount) + new string('.', barLength - filledCount);
    }

    /// <summary>
    /// Builds an aggregate progress bar for multiple accounts.
    /// </summary>
    /// <param name="remaining">The sum of remaining percentages.</param>
    /// <param name="capacity">The sum of total capacities.</param>
    /// <returns>The ASCII progress bar.</returns>
    private static string BuildAggregateBar(int remaining, int capacity)
    {
        const int barLength = ProxyConstants.Defaults.ProgressBarWidth;
        if (capacity <= 0)
        {
            return new string('.', barLength);
        }

        var clamped = Math.Clamp(remaining, 0, capacity);
        var filledCount = (int)Math.Round(clamped / (double)capacity * barLength, MidpointRounding.AwayFromZero);
        return new string('#', filledCount) + new string('.', barLength - filledCount);
    }

    /// <summary>
    /// Formats a Unix timestamp as a localized reset date/time.
    /// </summary>
    /// <param name="resetsAt">The Unix timestamp in seconds.</param>
    /// <returns>The formatted string.</returns>
    private static string FormatReset(long resetsAt)
    {
        return DateTimeOffset.FromUnixTimeSeconds(resetsAt).ToLocalTime().ToString("MMM dd, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renders a formatted ASCII table to the console.
    /// </summary>
    /// <param name="title">Optional table title.</param>
    /// <param name="headers">The column headers.</param>
    /// <param name="rows">The table data rows.</param>
    private static void RenderTable(string? title, string[] headers, string[][] rows)
    {
        var widths = headers
            .Select((header, index) => Math.Max(header.Length, rows.Max(row => row[index].Length)))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(title))
        {
            Console.WriteLine(title);
            Console.WriteLine();
        }

        Console.WriteLine(BuildTableRow(headers, widths));
        Console.WriteLine(BuildSeparator(widths));
        foreach (var row in rows)
        {
            Console.WriteLine(BuildTableRow(row, widths));
        }
    }

    /// <summary>
    /// Formats a single table row with pipe separators.
    /// </summary>
    /// <param name="cells">The row cells.</param>
    /// <param name="widths">The column widths.</param>
    /// <returns>The formatted row string.</returns>
    private static string BuildTableRow(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        var padded = cells.Select((cell, index) => cell.PadRight(widths[index]));
        return $"| {string.Join(" | ", padded)} |";
    }

    /// <summary>
    /// Builds an ASCII table separator line.
    /// </summary>
    /// <param name="widths">The column widths.</param>
    /// <returns>The separator string.</returns>
    private static string BuildSeparator(IReadOnlyList<int> widths)
    {
        return $"|-{string.Join("-|-", widths.Select(width => new string('-', width)))}-|";
    }

    /// <summary>
    /// Exchanges an OAuth authorization code for access and refresh tokens.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="verifier">The PKCE code verifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting token set and expiration.</returns>
    private async Task<TokenResult> ExchangeAuthorizationCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProxyConstants.Cli.ChatGptTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ProxyConstants.Cli.ChatGptClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = ProxyConstants.Cli.ChatGptRedirectUri
            })
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProxyConstants.ContentTypes.FormUrlEncoded);

        using var response = await _httpClientFactory.CreateClient("upstream").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ProxyConstants.Messages.FailedAuthorizationExchange);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            || !document.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement)
            || !document.RootElement.TryGetProperty("expires_in", out var expiresInElement))
        {
            throw new InvalidOperationException(ProxyConstants.Messages.InvalidOAuthTokenResponse);
        }

        return new TokenResult(
            accessTokenElement.GetString() ?? throw new InvalidOperationException(ProxyConstants.Messages.MissingAccessToken),
            refreshTokenElement.GetString() ?? throw new InvalidOperationException(ProxyConstants.Messages.MissingRefreshToken),
            DateTimeOffset.UtcNow.AddSeconds(expiresInElement.GetInt64()).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Parses the code and state from an OAuth callback URL or raw input.
    /// </summary>
    /// <param name="input">The input string to parse.</param>
    /// <returns>A tuple containing the code and state.</returns>
    private static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return (null, null);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            var query = ParseQueryString(absoluteUri.Query);
            return (query.TryGetValue("code", out var code) ? code : null, query.TryGetValue("state", out var state) ? state : null);
        }

        if (trimmed.Contains("code=", StringComparison.Ordinal))
        {
            var query = ParseQueryString(trimmed);
            return (query.TryGetValue("code", out var code) ? code : null, query.TryGetValue("state", out var state) ? state : null);
        }

        if (trimmed.Contains('#', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('#', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : null);
        }

        return (trimmed, null);
    }

    /// <summary>
    /// Parses a URL-encoded query string into a dictionary.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <returns>The parsed key-value pairs.</returns>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var normalized = query.TrimStart('?', '#');
        var pairs = normalized.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Generates a cryptographically secure PKCE code verifier.
    /// </summary>
    /// <returns>The base64-encoded verifier.</returns>
    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Generates a random session state for OAuth.
    /// </summary>
    /// <returns>The hex-encoded state.</returns>
    private static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a PKCE code challenge from a verifier using SHA256.
    /// </summary>
    /// <param name="verifier">The code verifier.</param>
    /// <returns>The base64-encoded challenge.</returns>
    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Attempts to extract an email address from JWT profile claims.
    /// </summary>
    /// <param name="token">The JWT string.</param>
    /// <returns>The email address if found; otherwise <see langword="null"/>.</returns>
    private static string? TryExtractEmailFromJwt(string token)
    {
        return JwtUtilities.TryReadStringClaim(token, "https://api.openai.com/profile", "email")
            ?? JwtUtilities.TryReadStringClaim(token, "email");
    }

    private sealed record ProviderLimitGroup(string ProviderId, LimitEntry[] Entries);
    private sealed record TokenResult(string Access, string Refresh, long ExpiresAt);
}
