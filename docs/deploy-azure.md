# Deploy to Azure

The server runs on **Azure App Service (Linux, .NET 10)** with:

| Resource | Purpose |
|----------|---------|
| App Service plan + Web App | Hosts the MCP endpoint (`/mcp`) and the OAuth broker (`/oauth/*`) |
| Azure SQL (Basic) | Encrypted per-user Projector sessions and pending OAuth flows |
| Key Vault (existing) | Projector OAuth client, JWT signing key, token encryption key, Entra app secrets |
| Log Analytics + Application Insights | Telemetry via the OpenTelemetry SDK |
| Entra app registrations | Tenant sign-in step, and a static OAuth client for Microsoft 365 Copilot |

The web app uses its **system-assigned managed identity** for Key Vault and SQL. There are no SQL passwords or Key Vault keys in app settings.

## Prerequisites

- An Azure subscription, and the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell 7
- Rights to: deploy to the resource group, create role assignments on the Key Vault, create Entra app registrations, and become the Azure SQL Entra admin (the signed-in user becomes the admin)
- A Projector OAuth app ([projector-oauth-setup.md](projector-oauth-setup.md))

## 1. Create the resource group and Key Vault

The scripts **never create or delete** the resource group or the Key Vault, so that a redeploy can never remove your secrets. Create them once:

```powershell
az group create -n rg-projector-mcp -l eastus2
az keyvault create -n contoso-projector-kv -g rg-projector-mcp -l eastus2 --enable-rbac-authorization true
```

Give yourself **Key Vault Secrets Officer** on the vault so the scripts can add secrets.

## 2. Store the Projector OAuth client in Key Vault

```powershell
az keyvault secret set --vault-name contoso-projector-kv --name ProjectorPSAOauthClientID --value "<client id>"
az keyvault secret set --vault-name contoso-projector-kv --name ProjectorPSAOauthSecret   --value "<client secret>"
```

The deploy script creates the other secrets if they are missing and never overwrites existing ones:

| Secret | Contents |
|--------|----------|
| `ProjectorMcpJwtSigningKey` | Signs the MCP Bearer JWTs |
| `ProjectorTokenEncryptionKey` | AES key for Projector session tickets at rest |
| `ProjectorMcpEntraClientId` / `ProjectorMcpEntraClientSecret` | Entra sign-in app |
| `ProjectorMcpOAuthClientId` / `ProjectorMcpOAuthClientSecret` | Static OAuth client for Microsoft 365 Copilot |

## 3. Fill in the deployment settings

```powershell
Copy-Item infrastructure/deploy.settings.example.psd1 infrastructure/deploy.settings.psd1
```

Edit `deploy.settings.psd1` (it is git-ignored). Required: `ResourceGroup`, `WebAppName`, `KeyVaultName`, `SqlServerName`, `AccountCode`. Everything else has a default; see the comments in the example file.

To keep the settings file somewhere else (for example in a private repository), pass `-SettingsFile <path>` to both scripts.

## 4. Deploy the infrastructure

```powershell
./infrastructure/Deploy-Infrastructure.ps1
```

The script:

1. Checks that the resource group and Key Vault exist.
2. Creates the missing Key Vault secrets and the two Entra app registrations.
3. Deploys `main.bicep` in **Incremental** mode (never Complete).
4. Merges the `Projector__*` app settings.
5. Creates the SQL contained user for the web app's managed identity.
6. Validates the managed identity wiring.

At the end it prints the **Projector callback URL**. Make sure it matches the redirect URL of your Projector OAuth app.

## 5. Publish the app

```powershell
./infrastructure/Publish-App.ps1
```

It builds `src/Projector.Mcp.Server`, zip-deploys it through Kudu, re-merges the app settings, restarts the app and waits for `/health`.

Re-run only this script for new versions of the server.

## 6. Smoke test

```powershell
$base = 'https://contoso-projector-mcp.azurewebsites.net'
Invoke-RestMethod "$base/health"      # status = ok, version = the server version that is running
Invoke-RestMethod "$base/health/components"   # one line per component; status Healthy
Invoke-RestMethod "$base/.well-known/oauth-authorization-server"
Invoke-RestMethod "$base/.well-known/oauth-protected-resource"
# Expect 401 with a WWW-Authenticate header:
try { Invoke-WebRequest "$base/mcp" -Method POST -ContentType 'application/json' -Body '{}' }
catch { $_.Exception.Response.StatusCode.value__ }
```

Then connect a client: [VS Code](clients/vscode.md), [Cursor](clients/cursor.md), [Claude](clients/claude.md). Your MCP server URL is `https://<WebAppName>.azurewebsites.net/mcp`.

## Sign-in flow

1. The MCP client opens `/oauth/authorize` in the browser.
2. **Microsoft Entra ID sign-in** (single-tenant app created by the deploy script). This restricts the server to users of your tenant and ties each Projector session to an Entra user. When `Projector:EntraClientId` / `Projector:EntraTenantId` are empty, this step is skipped.
3. **Projector sign-in** and consent.
4. The server stores the encrypted Projector session in SQL and returns an authorization code to the client, which exchanges it for a 1-hour JWT plus a refresh token (a new refresh token is returned on every refresh).

## Allowed redirect URIs

The OAuth broker only redirects to allowlisted client callbacks. Allowed by default:

- `https://claude.ai/api/mcp/auth_callback`, `https://claude.com/api/mcp/auth_callback` (Claude)
- `https://vscode.dev/redirect` (VS Code for the web)
- `https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect` (Microsoft 365 Copilot)
- Any `http://localhost:<port>` or `http://127.0.0.1:<port>` redirect (Claude Code, VS Code, Cursor, MCP Inspector)

Add others (for example a Copilot Studio connector callback) with `AllowedMcpRedirectUris` in the settings file. They are stored in the App Service setting `Projector__AllowedMcpRedirectUrisCsv` (semicolon-separated). `Publish-App.ps1` keeps entries that are already there.

## Reference catalogs

The server publishes static reference catalogs (cost centers, locations, departments/titles, custom fields) as MCP resources from `src/Projector.Mcp.Server/resources/catalogs/*.json`. The repository ships **sample** values. To publish your own, put files with the same names in a folder and set `CatalogsPath` in the settings file. `Publish-App.ps1` copies them over the samples in the build output.

## Operations notes

### Health endpoints

| Endpoint | Checks | Answer |
| --- | --- | --- |
| `GET /health` | Nothing: the process answers. This is the App Service health check path | `200 {status: ok, version, components: "/health/components"}`. The link is informational: calling `/health` never runs the component checks |
| `GET /health/components` | `service` (version, uptime), `configuration` (required settings present), `sql` (the token database answers), `keyVault` (the server's identity can read the JWT signing key's metadata, never a secret value), `projector` (the Projector sign-in host answers; API calls need a user and aren't tried), `appInsights` (connection string set; delivery isn't tested) | `200` when every component is `Healthy` or `Degraded`, `503` when one is `Unhealthy` |

Why two endpoints: App Service takes an instance out of rotation, and after an hour replaces it, when its health check path fails. A Projector or SQL outage would fail every instance at once, and a new instance can't fix it. So the App Service probe stays on `/health`, and the dependency checks run on `/health/components`, which you call yourself or point an availability test at.

`/health/components` needs no sign-in. It runs at most once every 30 seconds (later calls get the last answer, `cached: true`). Each check has a 5-second limit. The answer has fixed texts only: exception details go to the logs (`Component {name} is {status}` warnings).

### App settings get wiped

Two things can replace the whole App Service settings bag and take the server down (it falls back to localhost URLs and `/health` returns 503):

| Actor | What happens |
|-------|--------------|
| App Insights **codeless site extension** (`ApplicationInsightsAgent_EXTENSION_VERSION=~3`) | Can rewrite the bag to only `APPINSIGHTS_*` keys |
| Portal **Application Insights → Apply**, or a Bicep `sites/config/appsettings` resource | Full PUT; every key not included is deleted |

This repository avoids both:

- Telemetry uses the **OpenTelemetry SDK** with `APPLICATIONINSIGHTS_CONNECTION_STRING`.
- The site extension is set to `disabled`.
- App settings are applied only with `az webapp config appsettings set` (a merge), never by Bicep.

Don't enable Application Insights from the portal on this web app. If it happens, re-run `Publish-App.ps1`.

### Hard rules

1. Never delete the resource group or the Key Vault; the scripts refuse to create or delete them.
2. Deploy Bicep in **Incremental** mode only.
3. Don't rotate the Projector OAuth secrets casually. Every signed-in user has to reconnect after a rotation.

### Telemetry

| Source | Where |
|--------|-------|
| HTTP requests, dependencies (Projector SOAP), SQL client calls | Application Insights `<WebAppName>-ai` |
| Azure SQL platform metrics/logs | Log Analytics `<WebAppName>-law` (diagnostic setting `sql-to-law`) |
