namespace Projector.ApiClient;

public sealed class ProjectorOptions
{
    public const string SectionName = "Projector";

    /// <summary>Projector account code (the company code you type on the Projector sign-in page). Required.</summary>
    public string AccountCode { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    /// <summary>Registered redirect URI for the Projector OAuth app (callback into this server).</summary>
    public string RedirectUri { get; set; } = "http://localhost:5180/oauth/projector/callback";

    /// <summary>
    /// Projector OAuth scope. Prototypes use allowFullPermissions (cannot combine with other tags).
    /// </summary>
    public string RequestedScopes { get; set; } = "allowFullPermissions";

    public string AuthorizeBaseUrl { get; set; } = "https://app.projectorpsa.com/oauth2authorize";

    public string TokenUrl { get; set; } = "https://app.projectorpsa.com/oauth2token";

    public string RevokeUrl { get; set; } = "https://app.projectorpsa.com/oauth2revoketoken";

    /// <summary>Public base URL of this MCP server (for issuer/audience and protected-resource metadata).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5180";

    /// <summary>MCP JWT audience. Prefer a stable API name (e.g. projector-mcp) or full /mcp URL.</summary>
    public string McpAudience { get; set; } = "projector-mcp";

    /// <summary>MCP Bearer scope (this resource), not a Projector permission tag.</summary>
    public string McpScope { get; set; } = "projector.mcp";

    /// <summary>Base64 or plain key used to sign MCP JWTs. Development default only; Production loads from Key Vault.</summary>
    public string JwtSigningKey { get; set; } = "ProjectorMcpDevSigningKey_ChangeMe_32chars!";

    /// <summary>Base64 AES key for encrypting Projector tokens at rest. Empty = no SQL encryption (local only).</summary>
    public string TokenEncryptionKey { get; set; } = "";

    /// <summary>Azure Key Vault URI, e.g. https://my-vault.vault.azure.net/. Empty = skip KV load.</summary>
    public string? KeyVaultUri { get; set; }

    /// <summary>Secret name for the Projector OAuth client id.</summary>
    public string ClientIdSecretName { get; set; } = "ProjectorPSAOauthClientID";

    /// <summary>Secret name for the Projector OAuth client secret.</summary>
    public string ClientSecretSecretName { get; set; } = "ProjectorPSAOauthSecret";

    public string JwtSigningKeySecretName { get; set; } = "ProjectorMcpJwtSigningKey";

    public string TokenEncryptionKeySecretName { get; set; } = "ProjectorTokenEncryptionKey";

    public string EntraClientSecretSecretName { get; set; } = "ProjectorMcpEntraClientSecret";

    /// <summary>SQL connection string (Active Directory Managed Identity on Azure).</summary>
    public string? SqlConnectionString { get; set; }

    /// <summary>When true, use Azure SQL for connections and pending OAuth.</summary>
    public bool UseSqlTokenStore { get; set; }

    /// <summary>Entra app (client) ID for the authorize hop.</summary>
    public string EntraClientId { get; set; } = "";

    /// <summary>Entra client secret (from Key Vault in Production).</summary>
    public string EntraClientSecret { get; set; } = "";

    public string EntraTenantId { get; set; } = "";

    /// <summary>Static MCP OAuth client ID expected from Copilot / declarative agent.</summary>
    public string McpOAuthClientId { get; set; } = "";

    /// <summary>Semicolon- (or comma-) separated redirects from App Settings; merged into AllowedMcpRedirectUris.</summary>
    public string AllowedMcpRedirectUrisCsv { get; set; } = "";

    /// <summary>
    /// Allowed MCP OAuth client redirect URIs (confused-deputy mitigation). Loopback redirects
    /// (localhost / 127.0.0.1, any port) are always allowed for native clients.
    /// </summary>
    public string[] AllowedMcpRedirectUris { get; set; } =
    [
        "http://localhost:5180/oauth/dev/callback",
        "http://127.0.0.1:5180/oauth/dev/callback",
        "https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect",
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
        "https://vscode.dev/redirect"
    ];

    /// <summary>Allowed Origin headers for local Streamable HTTP (DNS rebinding protection).</summary>
    public string[] AllowedOrigins { get; set; } =
    [
        "http://localhost:5180",
        "http://127.0.0.1:5180",
        "http://localhost",
        "null"
    ];

    public IReadOnlyList<string> GetAllowedMcpRedirectUris()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in AllowedMcpRedirectUris)
        {
            if (!string.IsNullOrWhiteSpace(u))
            {
                set.Add(u.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(AllowedMcpRedirectUrisCsv))
        {
            foreach (var part in AllowedMcpRedirectUrisCsv.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }
        }

        return set.ToList();
    }
}
