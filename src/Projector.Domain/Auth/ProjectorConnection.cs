namespace Projector.Domain.Auth;

/// <summary>
/// Per-user Projector OAuth connection. The session ticket is never exposed to MCP clients.
/// </summary>
public sealed class ProjectorConnection
{
    public required string ConnectionId { get; init; }

    public string? TenantId { get; set; }

    public string? EntraObjectId { get; set; }

    public string? ProjectorAccountCode { get; set; }

    public required string SessionTicket { get; set; }

    public required string RefreshToken { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public required string GrantedScope { get; set; }

    public required string SoapServiceAuthority { get; set; }

    public required string RestServiceAuthority { get; set; }

    public string? DisplayName { get; set; }

    public string ConnectionStatus { get; set; } = "Active";

    public string EncryptionKeyVersion { get; set; } = "1";

    public byte[]? RowVersion { get; set; }

    public DateTimeOffset? RefreshInProgressUntil { get; set; }

    public IReadOnlySet<string> GrantedScopes =>
        GrantedScope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Projector user behind this connection: tenant + Entra object id + Projector account when known (hosted),
    /// else the connection id (local session). A connection belongs to exactly one user, so either identifies the user.
    /// </summary>
    public string UserKey =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(EntraObjectId)
            ? $"{TenantId}|{EntraObjectId}|{ProjectorAccountCode}"
            : $"conn|{ConnectionId}";

    public bool IsExpired(TimeSpan skew) => DateTimeOffset.UtcNow >= ExpiresAt - skew;

    public bool HasScope(string requiredScope) =>
        GrantedScopes.Contains("allowFullPermissions")
        || GrantedScopes.Contains(requiredScope);
}
