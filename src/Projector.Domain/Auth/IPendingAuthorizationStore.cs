namespace Projector.Domain.Auth;

public enum PendingKind
{
    McpCode,
    ProjectorLogin,
    CopilotAuthorize
}

/// <summary>Short-lived OAuth state for Copilot → MCP → Entra → Projector.</summary>
public sealed class PendingAuthorization
{
    public required string Code { get; init; }

    public required string ConnectionId { get; init; }

    public required string ClientRedirectUri { get; init; }

    public string? ClientState { get; init; }

    public string? ClientCodeChallenge { get; init; }

    public string? ClientId { get; init; }

    public string? ProjectorCodeVerifier { get; init; }

    public string? EntraNonce { get; init; }

    public string? TenantId { get; set; }

    public string? EntraObjectId { get; set; }

    public string? ProjectorAccountCode { get; set; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public PendingKind Kind { get; init; } = PendingKind.McpCode;

    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}

public sealed class PendingProjectorLogin
{
    public required string ConnectionId { get; init; }

    public required string ClientRedirectUri { get; init; }

    public string? ClientState { get; init; }

    public string? ClientCodeChallenge { get; init; }

    public string? ClientId { get; init; }

    public required string ProjectorCodeVerifier { get; init; }

    public string? EntraNonce { get; init; }

    public string? TenantId { get; init; }

    public string? EntraObjectId { get; init; }

    public string? ProjectorAccountCode { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public interface IPendingAuthorizationStore
{
    void Save(PendingAuthorization pending);

    /// <summary>Read a pending MCP auth code without consuming it.</summary>
    PendingAuthorization? Peek(string code);

    /// <summary>Consume a pending MCP auth code (one-time use).</summary>
    PendingAuthorization? Take(string code);

    void SaveByState(string state, PendingProjectorLogin login);

    PendingAuthorization? TakeByState(string state);

    void SaveFlow(string flowId, PendingAuthorization pending);

    PendingAuthorization? GetFlow(string flowId);

    void UpdateFlow(string flowId, PendingAuthorization pending);

    PendingAuthorization? TakeFlow(string flowId);
}
