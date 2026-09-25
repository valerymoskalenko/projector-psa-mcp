# Security

## Reporting a vulnerability

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/valerymoskalenko/projector-psa-mcp/security/advisories/new), not in public issues. Include steps to reproduce and the affected version (reported in the MCP `serverInfo`).

## Design summary

- **Read-only tools.** No tool writes to Projector PSA.
- **User-scoped access.** Each user signs in to Projector with OAuth; Projector enforces that user's permissions on every call. The `allowFullPermissions` scope never exceeds what the user already has.
- **Tokens stay on the server.** Projector session tickets are stored server-side, encrypted with AES (`ProjectorTokenEncryptionKey`) in Azure SQL. MCP clients receive only the server's own JWTs (1-hour lifetime) and refresh tokens.
- **Redirect allowlist.** The OAuth broker redirects only to allowlisted client callbacks and loopback addresses. Dynamic Client Registration rejects any other redirect URI. PKCE (S256) is supported and required by the MCP clients this server targets.
- **Origin check.** `/mcp` rejects requests with a browser `Origin` that is not allowlisted (DNS-rebinding protection).
- **Optional tenant restriction.** With the Entra sign-in step enabled, only users of your Microsoft Entra tenant can connect.
- **Managed identity.** In Azure, Key Vault and SQL are accessed with the web app's managed identity; there are no passwords in configuration.

## Operator checklist

- Keep secrets in Key Vault only. Never commit `deploy.settings.psd1` values that are secret, `.env` files, tokens or session caches.
- Grant users **Web Services Access = V** (view) unless a future write tool requires **U**.
- Rotate `ProjectorMcpJwtSigningKey` to invalidate all issued MCP tokens (users must reconnect).
