# MuxLlmProxy

Multi-account, multi-provider LLM proxy. Squeeze every last drop out of your accounts.

## Setup

```bash
git clone https://github.com/bariskisir/muxllmproxy.git
cd muxllmproxy
dotnet build -c Release src/MuxLlmProxy.slnx
```

## CLI

Add an account with:

You can add multiple

```bash
dotnet run -c Release --project src/MuxLlmProxy.Cli -- add
```

Example menu:

```text
Select a provider:
1. ChatGPT (Supports multi account)
2. OpenCode
3. OpenRouter
> 
```

## Multi-Account Routing

For providers with multi-account support, the proxy uses round-robin routing per requested model.

- each successful request advances to the next eligible account
- accounts with active weekly or temporary rate-limit cooldowns are skipped automatically

This makes it easy to spread traffic across multiple ChatGPT accounts.

## Limits

Check provider limits with:

```bash
dotnet run -c Release --project src/MuxLlmProxy.Cli -- limits
```

Example output:

```text
Done. Loaded 20 account limits.

| Provider   | Account               | Usage                  | Left | Resets       |
|------------|-----------------------|------------------------|------|--------------|
| chatgpt    | account_1@gmail.com   | [#################...] | 84%  | Apr 12, 09:10 |
| chatgpt    | account_2@gmail.com   | [#################...] | 83%  | Apr 12, 09:16 |
| chatgpt    | account_3@gmail.com   | [#################...] | 85%  | Apr 12, 09:24 |
| chatgpt    | account_4@gmail.com   | [################....] | 82%  | Apr 12, 09:31 |
| chatgpt    | account_5@gmail.com   | [#################...] | 84%  | Apr 12, 09:39 |
| chatgpt    | account_1@outlook.com | [#################...] | 83%  | Apr 12, 10:27 |
| chatgpt    | account_2@outlook.com | [#################...] | 84%  | Apr 12, 10:35 |
| chatgpt    | account_3@outlook.com | [################....] | 82%  | Apr 12, 10:42 |
| chatgpt    | account_4@outlook.com | [#################...] | 85%  | Apr 12, 10:50 |
| chatgpt    | account_5@outlook.com | [#################...] | 84%  | Apr 12, 10:58 |
```

## Run

Start the proxy with:

```bash
dotnet run -c Release --project src/MuxLlmProxy.Host
```

The server listens on `http://localhost:9000`.

If `ProxySettings:Token` in `src/MuxLlmProxy.Host/appsettings.json` contains a non-empty token, protected endpoints must include:

```text
Authorization: Bearer <token>
```

## Endpoints

`GET /healthz`

`GET /api/v1/models`

Example:

```bash
curl http://127.0.0.1:9000/api/v1/models
```

Example response:

```json
{
  "data": [
    {
      "id": "openrouter/free",
      "name": "OpenRouter Free"
    },
    {
      "id": "gpt-5.4",
      "name": "GPT-5.4"
    },
    {
      "id": "minimax-m2.5-free",
      "name": "MiniMax M2.5 Free"
    }
  ]
}
```

`GET /api/v1/limits`

Auth required.

Example:

```bash
curl http://127.0.0.1:9000/api/v1/limits \
  -H "Authorization: Bearer test"
```

Example response:

```json
{
  "data": [
    {
      "type_id": "opencode",
      "id": "20260405_020223",
      "has_limits": false
    },
    {
      "type_id": "openrouter",
      "id": "20260405_011533",
      "has_limits": false
    },
    {
      "type_id": "chatgpt",
      "id": "alpha@example.com",
      "has_limits": true,
      "limit": {
        "label": "Weekly limit",
        "left_percent": 72,
        "used_percent": 28,
        "resets_at": 1775876009,
        "window_duration_mins": 10080
      }
    },
    {
      "type_id": "chatgpt",
      "id": "beta@example.com",
      "has_limits": true,
      "limit": {
        "label": "Weekly limit",
        "left_percent": 99,
        "used_percent": 1,
        "resets_at": 1775855832,
        "window_duration_mins": 10080
      }
    }
  ]
}
```

`POST /api/v1/messages`

`POST /api/v1/chat/completions`

## Data Files

- `src/MuxLlmProxy.Host/data/accounts.json`: real accounts and credentials
- `src/MuxLlmProxy.Host/data/models.json`: provider model catalog
- `src/MuxLlmProxy.Host/appsettings.json`: runtime proxy settings

## Example OpenCode Config

```json
{
  "$schema": "https://opencode.ai/config.json",
  "small_model": "opencode/minimax-m2.5-free",
  "provider": {
    "mux-llm-proxy": {
      "name": "MuxLlmProxy",
      "options": {
        "baseURL": "http://127.0.0.1:9000/api/v1",
        "apiKey": "test",
        "timeout": 600000
      },
      "models": {
        "gpt-5.4": {},
        "gpt-5.4-mini": {},
        "gpt-5.3-codex": {},
        "gpt-5.2-codex": {},
        "gpt-5.2": {},
        "gpt-5.1-codex-max": {},
        "gpt-5.1-codex-mini": {},
        "openrouter/free": {},
        "qwen/qwen3.6-plus:free": {},
        "stepfun/step-3.5-flash:free": {},
        "minimax-m2.5-free": {},
        "qwen3.6-plus-free": {}
      }
    }
  }
}
```

## Example Claude Code Config

```text
ANTHROPIC_BASE_URL=http://127.0.0.1:9000/api
ANTHROPIC_MODEL=gpt-5.4
ANTHROPIC_DEFAULT_HAIKU_MODEL=gpt-5.4
ANTHROPIC_DEFAULT_SONNET_MODEL=gpt-5.4
ANTHROPIC_AUTH_TOKEN=test
ANTHROPIC_API_KEY=
```
```bash
npx claude-code-profile-switcher
```

```text
Other - 1
http://127.0.0.1:9000/api
gpt-5.4
test
```

Example usage:

```bash
claude --model gpt-5.4
claude --model qwen3.6-plus-free
claude --model minimax-m2.5-free
claude --model openrouter/free
```

## Docker

```bash
git clone https://github.com/bariskisir/muxllmproxy.git
cd muxllmproxy/src
docker build -t muxllmproxy -f MuxLlmProxy.Host/Dockerfile .
docker run -d --restart unless-stopped --name muxllmproxy -p 9000:9000 -v muxllmproxy_data:/app/data -e ProxySettings__Token=test muxllmproxy
```

The Docker image contains only the HTTP proxy host. Run CLI commands from the separate `MuxLlmProxy.Cli` project on the machine that manages `accounts.json`.


