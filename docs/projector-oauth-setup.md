# Projector PSA: OAuth app and user permissions

The server signs every user in to Projector PSA with OAuth 2.0 and calls Projector's web services with that user's session. You need one OAuth client application in Projector, and each user needs permission to use web services.

Projector documentation:

- [OAuth 2.0 Client Application Developer Guide](https://projectorpsa.atlassian.net/wiki/spaces/AD/pages/9372088/OAuth+2.0+Client+Application+Developer+Guide) — app registration, scopes, endpoints
- [Getting Started with Web Services 2.0](https://help.projectorpsa.com/display/AD/Getting+Started+with+Web+Services+2.0) — web services module and access
- [PwsAcquireOauth2Token](https://projectorpsa.atlassian.net/wiki/spaces/AD/pages/9374097/PwsAcquireOauth2Token) — how an OAuth token becomes a web services session

## 1. Register the OAuth client application

A Projector administrator registers the app in **Management Portal → Integration tab → OAuth panel**:

| Field | Value |
|-------|-------|
| Name / description | e.g. `Projector MCP Server` — users see this on the consent page |
| Redirect URL | `https://<your-web-app>.azurewebsites.net/oauth/projector/callback` |

Projector returns a **client ID** and **client secret**. Store them in Azure Key Vault (see [deploy-azure.md](deploy-azure.md#2-store-the-projector-oauth-client-in-key-vault)) — never in source control or app settings.

For local development, register a **second** app with the redirect URL `http://localhost:5180/oauth/projector/callback` (see [local-dev.md](local-dev.md)). The redirect URL must match exactly, so one app cannot serve both.

### Endpoints and account code

The server uses these Projector endpoints (defaults in `ProjectorOptions`):

| Endpoint | URL |
|----------|-----|
| Authorize | `https://app.projectorpsa.com/oauth2authorize/{account-code}` |
| Token | `https://app.projectorpsa.com/oauth2token` |
| Revoke | `https://app.projectorpsa.com/oauth2revoketoken` |

`{account-code}` is your Projector account (company) code — the value set in `Projector:AccountCode`. Only users of that account can sign in.

### Scope

The server requests the scope **`allowFullPermissions`** (`Projector:RequestedScopes`). Per Projector's guide, this grants the session *the maximal permissions available to the user* — never more than the user already has. It cannot be combined with other scope tags.

## 2. User permissions

**Users keep their current Projector permissions.** The MCP server does not add or elevate anything: a user sees through the AI assistant exactly the resources, projects, engagements and costs they can already see in Projector. Cost center permissions apply as usual.

On top of their current permissions, each user needs the global permission **Web Services Access**:

| Level | Meaning | Needed for |
|-------|---------|------------|
| **V** (View) | Read data through web services | **Enough for this server** — all tools are read-only |
| **U** (Update) | Read and write through web services | Not needed today; reserved for future write tools |

Notes:

- Web services must be enabled for your Projector account (the web services module). See [Getting Started with Web Services 2.0](https://help.projectorpsa.com/display/AD/Getting+Started+with+Web+Services+2.0).
- Web Services Access cannot be requested through an OAuth scope; it has to be granted to the user (or their permission profile) in Projector.
- Without Web Services Access, Projector rejects the web services calls the tools make, even if the sign-in itself succeeds.

## 3. Checklist

- [ ] OAuth app registered, redirect URL points at your deployed server
- [ ] Client ID and secret stored in Key Vault
- [ ] `AccountCode` set in your deployment settings
- [ ] Users (or their permission profiles) have **Web Services Access = V**
