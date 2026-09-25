# Local development

For contributors and for testing changes before deploying. End users should connect to the Azure deployment instead ([clients](../README.md#get-started)).

> **Windows only.** The local session cache is protected with Windows DPAPI (`%LOCALAPPDATA%\ProjectorMcp\oauth-sessions`).

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Projector OAuth app with the redirect URL `http://localhost:5180/oauth/projector/callback` ([projector-oauth-setup.md](projector-oauth-setup.md))

## 2. Configure

Use [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) so nothing lands in the repository:

```powershell
$p = 'src/Projector.Mcp.Server'
dotnet user-secrets set Projector:AccountCode  "<your account code>" --project $p
dotnet user-secrets set Projector:ClientId     "<dev client id>"     --project $p
dotnet user-secrets set Projector:ClientSecret "<dev client secret>" --project $p
```

Alternatively, keep the dev client in Key Vault: set `Projector:KeyVaultUri` instead of the client ID and secret. The secrets must be named `ProjectorPSADevOauthClientID` and `ProjectorPSADevOauthSecret` (see `appsettings.Development.json`). Your `az login` identity needs read access to them.

Set the environment once per terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
```

## 3. Sign in and run tools from the CLI

```powershell
$p = 'src/Projector.Mcp.Server'
dotnet run --project $p --no-launch-profile -- auth login          # browser sign-in, caches the session
dotnet run --project $p --no-launch-profile -- tool get_resource --full-name "Jane Doe"
dotnet run --project $p --no-launch-profile -- tool list_upcoming_pto --resource-id 10001 --start-date 2026-09-01 --end-date 2026-12-30
dotnet run --project $p --no-launch-profile -- auth logout         # revoke and clear the cache
```

Tool arguments are the tool's parameters in `--kebab-case` (see `src/Projector.Mcp.Server/tools.json`).

## 4. Run as an MCP server

**stdio** (uses the session from `auth login`). The repository includes ready-made configs: `.vscode/mcp.json` (VS Code), `.cursor/mcp.json` (Cursor), `.mcp.json` (Claude Code). They run:

```text
dotnet run --project src/Projector.Mcp.Server/Projector.Mcp.Server.csproj --no-launch-profile --verbosity quiet -- serve --stdio
```

**HTTP** on `http://127.0.0.1:5180/mcp`, with the full OAuth flow (no Entra step locally; in-memory token store):

```powershell
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- serve --http
```

Point [MCP Inspector](https://github.com/modelcontextprotocol/inspector) or any client at `http://localhost:5180/mcp`.

## 5. Tests

```powershell
dotnet test tests/Projector.UnitTests --filter "Category!=Live"   # offline: fixtures, protocol, catalog
```

Live tests call your real Projector account (read-only), using the session from `auth login`. They need a few environment variables that describe your data:

```powershell
$env:PROJECTOR_LIVE_ACCOUNT_CODE = '<account code>'
$env:PROJECTOR_LIVE_RESOURCES    = '10001:jane.doe@contoso.com;10002'   # first = primary resource
$env:PROJECTOR_LIVE_PROJECT_CODE = 'P001234-001'
dotnet test tests/Projector.UnitTests --filter "Category=Live"
```

Optional: `PROJECTOR_LIVE_RESOURCE_SEARCH`, `PROJECTOR_LIVE_PTO_COUNT`, `PROJECTOR_LIVE_KEYVAULT_URI`. See `LiveSettings` in `tests/Projector.UnitTests/LiveCachedToolTests.cs`.

## Build locks

If a local MCP session (stdio) is running from your editor, `dotnet build` can fail with "file is locked by Projector.Mcp.Server". Stop the MCP server in the editor, or build to another folder: `dotnet test --artifacts-path <folder>`.
