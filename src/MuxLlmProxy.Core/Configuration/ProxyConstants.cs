namespace MuxLlmProxy.Core.Configuration;

/// <summary>
/// Provides shared constants used across the proxy solution.
/// </summary>
public static class ProxyConstants
{
    /// <summary>
    /// Provides default runtime values.
    /// </summary>
    public static class Defaults
    {
        public const int Port = 9000;
        public const int TimeoutSeconds = 60;
        public const int WeeklyLimitSyncIntervalMinutes = 10;
        public const int RetryAfterFallbackMinutes = 15;
        public const int LogRetentionDays = 31;
        public const int WeeklyLimitWindowMinutes = 10080;
        public const int WeeklyLimitPercent = 100;
        public const int LogBodyCharacterLimit = 4096;
        public const int ProgressBarWidth = 20;
    }

    /// <summary>
    /// Provides shared directory and file names.
    /// </summary>
    public static class Paths
    {
        public const string DataDirectoryName = "data";
        public const string AccountsFileName = "accounts.json";
        public const string ModelsFileName = "models.json";
        public const string LogsDirectoryName = "logs";
        public const string ProxyLogFilePattern = "proxy-.log";
        public const string HostProjectDirectoryName = "MuxLlmProxy.Host";
        public const string CliProjectDirectoryName = "MuxLlmProxy.Cli";
    }

    /// <summary>
    /// Provides shared content types.
    /// </summary>
    public static class ContentTypes
    {
        public const string Json = "application/json";
        public const string EventStream = "text/event-stream";
        public const string EventStreamUtf8 = "text/event-stream; charset=utf-8";
        public const string FormUrlEncoded = "application/x-www-form-urlencoded";
    }

    /// <summary>
    /// Provides shared HTTP header names.
    /// </summary>
    public static class Headers
    {
        public const string Authorization = "Authorization";
        public const string Cookie = "Cookie";
        public const string SetCookie = "Set-Cookie";
        public const string XApiKey = "X-Api-Key";
        public const string ApiKey = "Api-Key";
        public const string ContentType = "content-type";
        public const string RetryAfter = "retry-after";
        public const string RateLimitReset = "x-ratelimit-reset";
        public const string RateLimitResetRequests = "x-ratelimit-reset-requests";
        public const string ChatGptAccountId = "chatgpt-account-id";
        public const string Originator = "originator";
        public const string OpenCodeSession = "x-opencode-session";
    }

    /// <summary>
    /// Provides shared provider identifiers.
    /// </summary>
    public static class Providers
    {
        public const string ChatGpt = "chatgpt";
        public const string OpenCode = "opencode";
        public const string OpenRouter = "openrouter";
    }

    /// <summary>
    /// Provides shared provider header values.
    /// </summary>
    public static class ProviderHeaders
    {
        public const string CodexOriginator = "codex_cli_rs";
        public const string OpenCodeSessionValue = "1";
    }

    /// <summary>
    /// Provides CLI and OAuth-specific values.
    /// </summary>
    public static class Cli
    {
        public const string ChatGptAuthorizeUrl = "https://auth.openai.com/oauth/authorize";
        public const string ChatGptTokenUrl = "https://auth.openai.com/oauth/token";
        public const string ChatGptClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
        public const string ChatGptRedirectUri = "http://localhost:1455/auth/callback";
        public const string ChatGptScope = "openid profile email offline_access";
        public const string PublicToken = "public";
        public const string AddCommand = "add";
        public const string LimitsCommand = "limits";
    }

    /// <summary>
    /// Provides shared route segments.
    /// </summary>
    public static class Routes
    {
        public const string Health = "/healthz";
        public const string Models = "/api/v1/models";
        public const string Limits = "/api/v1/limits";
        public const string Messages = "/api/v1/messages";
        public const string ChatCompletions = "/api/v1/chat/completions";
    }

    /// <summary>
    /// Provides shared response literals.
    /// </summary>
    public static class Responses
    {
        public const string Healthy = "ok";
        public const string DefaultInstructions = "You are a helpful assistant.";
        public const string BearerScheme = "Bearer";
    }

    /// <summary>
    /// Provides shared display strings.
    /// </summary>
    public static class Labels
    {
        public const string WeeklyLimit = "Weekly limit";
        public const string Provider = "Provider";
        public const string Account = "Account";
        public const string Left = "Left";
        public const string Resets = "Resets";
        public const string Unavailable = "Unavailable";
        public const string Unknown = "Unknown";
        public const string ChatGpt = "ChatGPT";
        public const string OpenCode = "OpenCode";
        public const string OpenRouter = "OpenRouter";
    }

    /// <summary>
    /// Provides shared user-facing messages.
    /// </summary>
    public static class Messages
    {
        public const string AuthRequired = "A valid bearer token is required.";
        public const string CliCommandRequired = "A CLI command is required. Supported commands: add, limits.";
        public const string NoProvidersAvailable = "No providers are available for account setup.";
        public const string SelectProvider = "Select a provider:";
        public const string InvalidSelection = "Invalid selection.";
        public const string OpenRouterTokenPrompt = "OpenRouter token: ";
        public const string ValueRequired = "Value is required.";
        public const string OpenLoginUrl = "Open this URL and complete login:";
        public const string PasteCallbackUrlPrompt = "Paste the full callback URL here: ";
        public const string MissingAuthorizationCode = "Missing authorization code in pasted callback URL.";
        public const string OAuthStateMismatch = "OAuth state mismatch.";
        public const string NoAccountsConfigured = "No accounts configured.";
        public const string FailedAuthorizationExchange = "Failed to exchange authorization code.";
        public const string InvalidOAuthTokenResponse = "Invalid OAuth token response.";
        public const string MissingAccessToken = "Missing access token.";
        public const string MissingRefreshToken = "Missing refresh token.";
        public const string LoadedAccountLimitsFormat = "Loaded {0} account limits.";
        public const string UnsupportedCliCommandFormat = "Unsupported CLI command '{0}'. Supported commands: add, limits.";
    }
}
