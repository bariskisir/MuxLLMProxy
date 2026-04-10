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
        /// <summary>The default port for the proxy host.</summary>
        public const int Port = 9000;

        /// <summary>The default request timeout in seconds.</summary>
        public const int TimeoutSeconds = 60;

        /// <summary>The interval between weekly limit synchronizations in minutes.</summary>
        public const int WeeklyLimitSyncIntervalMinutes = 10;

        /// <summary>The fallback cooldown duration in minutes after a rate-limit error.</summary>
        public const int RetryAfterFallbackMinutes = 15;

        /// <summary>The number of days to retain rotated log files.</summary>
        public const int LogRetentionDays = 31;

        /// <summary>The number of minutes in a standard weekly limit window.</summary>
        public const int WeeklyLimitWindowMinutes = 10080;

        /// <summary>The target percentage (100%) for weekly limit calculations.</summary>
        public const int WeeklyLimitPercent = 100;

        /// <summary>The maximum number of characters to log for request/response bodies.</summary>
        public const int LogBodyCharacterLimit = 4096;

        /// <summary>The width of the CLI progress bar in characters.</summary>
        public const int ProgressBarWidth = 20;
    }

    /// <summary>
    /// Provides shared directory and file names.
    /// </summary>
    public static class Paths
    {
        /// <summary>The name of the data storage directory.</summary>
        public const string DataDirectoryName = "data";

        /// <summary>The filename for the accounts database.</summary>
        public const string AccountsFileName = "accounts.json";

        /// <summary>The filename for the model catalog.</summary>
        public const string ModelsFileName = "models.json";

        /// <summary>The name of the log storage directory.</summary>
        public const string LogsDirectoryName = "logs";

        /// <summary>The file pattern for rotating proxy logs.</summary>
        public const string ProxyLogFilePattern = "proxy-.log";

        /// <summary>The project name for the host application.</summary>
        public const string HostProjectDirectoryName = "MuxLlmProxy.Host";

        /// <summary>The project name for the CLI tool.</summary>
        public const string CliProjectDirectoryName = "MuxLlmProxy.Cli";
    }

    /// <summary>
    /// Provides shared content types.
    /// </summary>
    public static class ContentTypes
    {
        /// <summary>Standard JSON content type.</summary>
        public const string Json = "application/json";

        /// <summary>Server-Sent Events content type.</summary>
        public const string EventStream = "text/event-stream";

        /// <summary>Server-Sent Events content type with UTF-8 encoding.</summary>
        public const string EventStreamUtf8 = "text/event-stream; charset=utf-8";

        /// <summary>Form URL encoded content type.</summary>
        public const string FormUrlEncoded = "application/x-www-form-urlencoded";
    }

    /// <summary>
    /// Provides shared HTTP header names.
    /// </summary>
    public static class Headers
    {
        /// <summary>The Authorization header.</summary>
        public const string Authorization = "Authorization";

        /// <summary>The Cookie header.</summary>
        public const string Cookie = "Cookie";

        /// <summary>The Set-Cookie header.</summary>
        public const string SetCookie = "Set-Cookie";

        /// <summary>The X-Api-Key header.</summary>
        public const string XApiKey = "X-Api-Key";

        /// <summary>The Api-Key header.</summary>
        public const string ApiKey = "Api-Key";

        /// <summary>The Content-Type header.</summary>
        public const string ContentType = "content-type";

        /// <summary>The Retry-After header.</summary>
        public const string RetryAfter = "retry-after";

        /// <summary>The X-Ratelimit-Reset header.</summary>
        public const string RateLimitReset = "x-ratelimit-reset";

        /// <summary>The X-Ratelimit-Reset-Requests header.</summary>
        public const string RateLimitResetRequests = "x-ratelimit-reset-requests";

        /// <summary>The ChatGPT specific account header.</summary>
        public const string ChatGptAccountId = "chatgpt-account-id";

        /// <summary>The OpenAI-Beta header.</summary>
        public const string OpenAiBeta = "OpenAI-Beta";

        /// <summary>The originator header.</summary>
        public const string Originator = "originator";

        /// <summary>The session ID header.</summary>
        public const string SessionId = "session_id";

        /// <summary>The conversation ID header.</summary>
        public const string ConversationId = "conversation_id";

        /// <summary>The OpenCode specific session header.</summary>
        public const string OpenCodeSession = "x-opencode-session";

        /// <summary>The Claude Code session identifier header.</summary>
        public const string ClaudeCodeSessionId = "x-claude-code-session-id";

        /// <summary>The session affinity header.</summary>
        public const string SessionAffinity = "x-session-affinity";
    }

    /// <summary>
    /// Provides shared provider identifiers.
    /// </summary>
    public static class Providers
    {
        /// <summary>Identifier for the ChatGPT provider.</summary>
        public const string ChatGpt = "chatgpt";

        /// <summary>Identifier for the OpenCode provider.</summary>
        public const string OpenCode = "opencode";

        /// <summary>Identifier for the OpenRouter provider.</summary>
        public const string OpenRouter = "openrouter";
    }

    /// <summary>
    /// Provides shared provider header values.
    /// </summary>
    public static class ProviderHeaders
    {
        /// <summary>The originator value for OpenCode/Codex.</summary>
        public const string CodexOriginator = "codex_cli_rs";

        /// <summary>The OpenAI-Beta value for experimental responses.</summary>
        public const string OpenAiBetaResponses = "responses=experimental";

        /// <summary>The default value for OpenCode session headers.</summary>
        public const string OpenCodeSessionValue = "1";
    }

    /// <summary>
    /// Provides CLI and OAuth-specific values.
    /// </summary>
    public static class Cli
    {
        /// <summary>The ChatGPT OAuth authorization URL.</summary>
        public const string ChatGptAuthorizeUrl = "https://auth.openai.com/oauth/authorize";

        /// <summary>The ChatGPT OAuth token exchange URL.</summary>
        public const string ChatGptTokenUrl = "https://auth.openai.com/oauth/token";

        /// <summary>The public client identifier for ChatGPT OAuth.</summary>
        public const string ChatGptClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

        /// <summary>The OAuth redirect URI for the CLI login flow.</summary>
        public const string ChatGptRedirectUri = "http://localhost:1455/auth/callback";

        /// <summary>The OAuth scopes required for ChatGPT authentication.</summary>
        public const string ChatGptScope = "openid profile email offline_access";

        /// <summary>The reserved token value for public/anonymous access.</summary>
        public const string PublicToken = "public";

        /// <summary>The CLI command for adding accounts.</summary>
        public const string AddCommand = "add";

        /// <summary>The CLI command for listing limits.</summary>
        public const string LimitsCommand = "limits";
    }

    /// <summary>
    /// Provides shared route segments.
    /// </summary>
    public static class Routes
    {
        /// <summary>The health check route.</summary>
        public const string Health = "/healthz";

        /// <summary>The Anthropic-style models listing route.</summary>
        public const string Models = "/api/v1/models";

        /// <summary>The OpenAI-style models listing route.</summary>
        public const string OpenAiModels = "/v1/models";

        /// <summary>The proxy limits discovery route.</summary>
        public const string Limits = "/api/v1/limits";

        /// <summary>The Anthropic messages endpoint.</summary>
        public const string Messages = "/api/v1/messages";

        /// <summary>The Anthropic chat completions endpoint.</summary>
        public const string ChatCompletions = "/api/v1/chat/completions";

        /// <summary>The OpenAI chat completions endpoint.</summary>
        public const string OpenAiChatCompletions = "/v1/chat/completions";

        /// <summary>The ChatGPT backend-api responses endpoint.</summary>
        public const string Responses = "/v1/responses";
    }

    /// <summary>
    /// Provides shared response literals.
    /// </summary>
    public static class Responses
    {
        /// <summary>The standard healthy status response.</summary>
        public const string Healthy = "ok";

        /// <summary>Default system instructions for chat sessions.</summary>
        public const string DefaultInstructions = "You are a helpful assistant.";

        /// <summary>The Bearer authentication scheme.</summary>
        public const string BearerScheme = "Bearer";
    }

    /// <summary>
    /// Provides shared display strings.
    /// </summary>
    public static class Labels
    {
        /// <summary>Label for weekly usage limits.</summary>
        public const string WeeklyLimit = "Weekly limit";

        /// <summary>Label for the Provider field.</summary>
        public const string Provider = "Provider";

        /// <summary>Label for the Account field.</summary>
        public const string Account = "Account";

        /// <summary>Label for remaining capacity.</summary>
        public const string Left = "Left";

        /// <summary>Label for the reset timestamp.</summary>
        public const string Resets = "Resets";

        /// <summary>Display string for unavailable items.</summary>
        public const string Unavailable = "Unavailable";

        /// <summary>Display string for unknown values.</summary>
        public const string Unknown = "Unknown";

        /// <summary>Display name for ChatGPT.</summary>
        public const string ChatGpt = "ChatGPT";

        /// <summary>Display name for OpenCode.</summary>
        public const string OpenCode = "OpenCode";

        /// <summary>Display name for OpenRouter.</summary>
        public const string OpenRouter = "OpenRouter";
    }

    /// <summary>
    /// Provides shared user-facing messages.
    /// </summary>
    public static class Messages
    {
        /// <summary>Error message when authorization is missing.</summary>
        public const string AuthRequired = "A valid bearer token is required.";

        /// <summary>Error message when a CLI command is missing.</summary>
        public const string CliCommandRequired = "A CLI command is required. Supported commands: add, limits.";

        /// <summary>Error message when no providers are configured.</summary>
        public const string NoProvidersAvailable = "No providers are available for account setup.";

        /// <summary>Prompt for provider selection.</summary>
        public const string SelectProvider = "Select a provider:";

        /// <summary>Error message for invalid user selection.</summary>
        public const string InvalidSelection = "Invalid selection.";

        /// <summary>Prompt for OpenRouter API token.</summary>
        public const string OpenRouterTokenPrompt = "OpenRouter token: ";

        /// <summary>Validation error message for required fields.</summary>
        public const string ValueRequired = "Value is required.";

        /// <summary>Instruction to open the OAuth URL.</summary>
        public const string OpenLoginUrl = "Open this URL and complete login:";

        /// <summary>Prompt for the pasted callback URL.</summary>
        public const string PasteCallbackUrlPrompt = "Paste the full callback URL here: ";

        /// <summary>Error message when the authorization code is missing.</summary>
        public const string MissingAuthorizationCode = "Missing authorization code in pasted callback URL.";

        /// <summary>Error message for OAuth state mismatch.</summary>
        public const string OAuthStateMismatch = "OAuth state mismatch.";

        /// <summary>Notice when no accounts are found.</summary>
        public const string NoAccountsConfigured = "No accounts configured.";

        /// <summary>Error message for failed authorization exchange.</summary>
        public const string FailedAuthorizationExchange = "Failed to exchange authorization code.";

        /// <summary>Error message for invalid OAuth token responses.</summary>
        public const string InvalidOAuthTokenResponse = "Invalid OAuth token response.";

        /// <summary>Error message for missing access tokens.</summary>
        public const string MissingAccessToken = "Missing access token.";

        /// <summary>Error message for missing refresh tokens.</summary>
        public const string MissingRefreshToken = "Missing refresh token.";

        /// <summary>Format string for logging loaded limit counts.</summary>
        public const string LoadedAccountLimitsFormat = "Loaded {0} account limits.";

        /// <summary>Format string for unsupported CLI commands.</summary>
        public const string UnsupportedCliCommandFormat = "Unsupported CLI command '{0}'. Supported commands: add, limits.";
    }
}
