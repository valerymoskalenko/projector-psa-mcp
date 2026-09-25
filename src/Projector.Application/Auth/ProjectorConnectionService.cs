using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Application.Auth;

public sealed class ProjectorConnectionService
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly IProjectorConnectionStore _store;
    private readonly IProjectorTokenClient _tokenClient;

    public ProjectorConnectionService(
        IProjectorConnectionStore store,
        IProjectorTokenClient tokenClient)
    {
        _store = store;
        _tokenClient = tokenClient;
    }

    public async Task<ProjectorConnection> RequireConnectionAsync(
        string connectionId,
        string? requiredScope,
        CancellationToken cancellationToken = default)
    {
        var connection = _store.Get(connectionId)
            ?? throw new ProjectorAuthorizationException(
                "No Projector connection for this caller. Reconnect via OAuth.");

        if (connection.IsExpired(RefreshSkew))
        {
            connection = await RefreshConnectionAsync(connection, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(requiredScope) && !connection.HasScope(requiredScope))
        {
            throw new ProjectorAuthorizationException(
                $"The connected Projector user has not granted the required permission '{requiredScope}'.");
        }

        return connection;
    }

    public async Task<ProjectorConnection> RefreshConnectionAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default)
    {
        var token = await _tokenClient.RefreshAsync(connection, cancellationToken);
        ApplyToken(connection, token);
        _store.Save(connection);
        return connection;
    }

    public ProjectorConnection CreateFromToken(
        string connectionId,
        ProjectorTokenResponse token,
        string? displayName = null)
    {
        var connection = new ProjectorConnection
        {
            ConnectionId = connectionId,
            SessionTicket = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            GrantedScope = token.Scope,
            SoapServiceAuthority = token.SoapServiceAuthority.TrimEnd('/'),
            RestServiceAuthority = token.RestServiceAuthority.TrimEnd('/'),
            DisplayName = displayName
        };
        _store.Save(connection);
        return connection;
    }

    /// <summary>
    /// Upsert Projector tickets for an Entra owner. Reuses the existing connection id when
    /// (tid, oid, account) already exists so reconnect does not violate UX_ProjectorConnections_Owner.
    /// </summary>
    public ProjectorConnection UpsertFromToken(
        string proposedConnectionId,
        ProjectorTokenResponse token,
        string? tenantId,
        string? entraObjectId,
        string? projectorAccountCode,
        string? displayName = null)
    {
        var connectionId = proposedConnectionId;
        if (!string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(entraObjectId)
            && !string.IsNullOrWhiteSpace(projectorAccountCode))
        {
            var existing = _store.GetByOwner(tenantId, entraObjectId, projectorAccountCode);
            if (existing is not null)
            {
                connectionId = existing.ConnectionId;
            }
        }

        var connection = new ProjectorConnection
        {
            ConnectionId = connectionId,
            TenantId = tenantId,
            EntraObjectId = entraObjectId,
            ProjectorAccountCode = projectorAccountCode,
            SessionTicket = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            GrantedScope = token.Scope,
            SoapServiceAuthority = token.SoapServiceAuthority.TrimEnd('/'),
            RestServiceAuthority = token.RestServiceAuthority.TrimEnd('/'),
            DisplayName = displayName,
            ConnectionStatus = "Active"
        };
        _store.Save(connection);
        return connection;
    }

    public void Save(ProjectorConnection connection) => _store.Save(connection);

    public static void ApplyToken(ProjectorConnection connection, ProjectorTokenResponse token)
    {
        connection.SessionTicket = token.AccessToken;
        connection.RefreshToken = token.RefreshToken;
        connection.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        connection.GrantedScope = token.Scope;
        connection.SoapServiceAuthority = token.SoapServiceAuthority.TrimEnd('/');
        connection.RestServiceAuthority = token.RestServiceAuthority.TrimEnd('/');
    }
}
