# Security

## Reporting a vulnerability

Please report vulnerabilities privately through [GitHub Security Advisories](https://github.com/valerymoskalenko/projector-psa-mcp/security/advisories/new), not in public issues. Include steps to reproduce and the affected version (reported in the MCP `serverInfo`).

## Design summary

- **Two write tools, drafts only.** `save_timecard` saves work time cards on the signed-in user's own time sheet (it never sends a resource, so Projector applies it to the caller), creates cards as Draft, updates only Draft or Rejected cards, and never submits, approves or deletes. `save_expenses` does the same for cost cards and receipts on the user's own expense reports. A save is sent once and never retried automatically. All other tools are read-only.
- **Receipt upload endpoint.** `POST /receipts/upload` takes a receipt file for the caller's own receipt pool, like Projector's `AjxAddDocument`. It accepts no MCP token, only an upload ticket from `list_expenses`: HMAC-signed with a key derived from `ProjectorMcpJwtSigningKey`, in its own format (never valid as an MCP token, and the other way round), bound to one connection, valid 30 minutes and for 50 uploads. Bodies over 3 MB are refused; the file type is checked from its bytes (PDF, PNG, JPEG, GIF).
- **Receipt links.** `save_expenses` can download a receipt from a public `https` link (`receipt.source_url`). To keep the server from reaching anything internal, only port 443 is used, user names in URLs are refused, and every address the server connects to (after DNS, on each of at most 3 redirects) must be public: private, loopback, link-local (including the cloud metadata address), carrier-grade NAT and multicast ranges are refused. No cookies, credentials or proxies are used; 15 s and the receipt quota are the limits.
- **User-scoped access.** Each user signs in to Projector with OAuth; Projector enforces that user's permissions on every call. The `allowFullPermissions` scope never exceeds what the user already has.
- **Tokens stay on the server.** Projector session tickets are stored server-side, encrypted with AES (`ProjectorTokenEncryptionKey`) in Azure SQL. MCP clients receive only the server's own JWTs (1-hour lifetime) and refresh tokens.
- **Redirect allowlist.** The OAuth broker redirects only to allowlisted client callbacks and loopback addresses. Dynamic Client Registration rejects any other redirect URI. PKCE (S256) is supported and required by the MCP clients this server targets.
- **Origin check.** `/mcp` rejects requests with a browser `Origin` that is not allowlisted (DNS-rebinding protection).
- **Optional tenant restriction.** With the Entra sign-in step enabled, only users of your Microsoft Entra tenant can connect.
- **Managed identity.** In Azure, Key Vault and SQL are accessed with the web app's managed identity; there are no passwords in configuration.

## Operator checklist

- Keep secrets in Key Vault only. Never commit `deploy.settings.psd1` values that are secret, `.env` files, tokens or session caches.
- Grant users **Web Services Access = V** (view) for the read tools. Users who should log time with `save_timecard` need **U** (update); with V the save fails with `web_services_access_view_only` (Projector: `UpdatePermissionDenied`).
- Rotate `ProjectorMcpJwtSigningKey` to invalidate all issued MCP tokens (users must reconnect) and all receipt upload tickets.
