# Ara Composio MCP Proxy (Foundry-Compatible)

This Azure Functions app acts as a reverse-proxy in front of Composio MCP and **sanitizes** tool schemas so Microsoft Foundry doesn't reject the toolset.

## What it fixes
Foundry may throw: `Invalid tool schema for: ComposioBackend.TAVILY_SEARCH` (or other tools).
This proxy filters out known-bad tools and rejects tools with schemas that violate Foundry's stricter JSON schema requirements.

## Local run
1. Open in Visual Studio 2022 (or VS Code).
2. Set `local.settings.json` values:
   - COMPOSIO_MCP_URL (include user_id query param)
   - COMPOSIO_API_KEY
3. Run (Functions Core Tools required).

## Environment variables (Azure App Settings)
- COMPOSIO_MCP_URL (required)
- COMPOSIO_API_KEY (required)  **Store in Key Vault ideally**
- COMPOSIO_API_KEY_HEADER (default: x-api-key)
- MCP_BLOCK_TOOL_NAMES (default includes ComposioBackend.TAVILY_SEARCH)
- MCP_ALLOW_TOOL_NAMES (optional allowlist; if empty -> allow all minus blocklist)
- MCP_ALLOW_TOOL_REGEX (optional; comma-separated regex patterns)

## Foundry configuration
Use the Function's endpoint as MCP server URL:
- `https://<functionapp>.azurewebsites.net/api/mcp`

Auth:
- Key-based
- Header name: `x-api-key` (your **internal** key if you add APIM; otherwise none)
Then the proxy injects the real Composio key upstream.

## Hardening (recommended)
- Put APIM in front and require an internal key from Foundry.
- Store COMPOSIO_API_KEY in Key Vault and reference it from Function App settings.
