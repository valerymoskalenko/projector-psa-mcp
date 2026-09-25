using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Projector.Application.Auth;
using Projector.Domain.Auth;

namespace Projector.Application.Persistence;

public sealed class SqlProjectorConnectionStore : IProjectorConnectionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenEncryptionService _encryption;
    private readonly IMemoryCache _cache;

    public SqlProjectorConnectionStore(
        IServiceScopeFactory scopeFactory,
        TokenEncryptionService encryption,
        IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _encryption = encryption;
        _cache = cache;
    }

    public void Save(ProjectorConnection connection)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tenantId = connection.TenantId ?? "";
        var oid = connection.EntraObjectId ?? "";
        var account = connection.ProjectorAccountCode ?? "";

        // Orphan rows from older CreateFromToken paths used empty owner keys and poison
        // UX_ProjectorConnections_Owner (duplicate key (, , )). Remove them before upsert.
        var emptyOwnerOrphans = db.Connections
            .Where(c => c.TenantId == "" && c.EntraObjectId == "" && c.ProjectorAccountCode == "")
            .ToList();
        if (emptyOwnerOrphans.Count > 0)
        {
            db.Connections.RemoveRange(emptyOwnerOrphans);
        }

        ProjectorConnectionEntity? existing = null;

        // Prefer owner match when identity is known (reconnect / second Connect).
        if (!string.IsNullOrEmpty(tenantId)
            && !string.IsNullOrEmpty(oid)
            && !string.IsNullOrEmpty(account))
        {
            existing = db.Connections.FirstOrDefault(c =>
                c.TenantId == tenantId
                && c.EntraObjectId == oid
                && c.ProjectorAccountCode == account);
        }

        existing ??= db.Connections.FirstOrDefault(c => c.ConnectionId == connection.ConnectionId);

        if (existing is null)
        {
            existing = new ProjectorConnectionEntity
            {
                ConnectionId = connection.ConnectionId,
                CreatedAt = now
            };
            db.Connections.Add(existing);
        }

        existing.TenantId = tenantId;
        existing.EntraObjectId = oid;
        existing.ProjectorAccountCode = account;
        existing.EncryptedAccessToken = _encryption.Encrypt(connection.SessionTicket);
        existing.EncryptedRefreshToken = _encryption.Encrypt(connection.RefreshToken);
        existing.AccessTokenExpiresAt = connection.ExpiresAt;
        existing.GrantedScopes = connection.GrantedScope;
        existing.RestServiceAuthority = connection.RestServiceAuthority;
        existing.SoapServiceAuthority = connection.SoapServiceAuthority;
        existing.ConnectionStatus = string.IsNullOrWhiteSpace(connection.ConnectionStatus)
            ? "Active"
            : connection.ConnectionStatus;
        existing.EncryptionKeyVersion = _encryption.KeyVersion;
        existing.DisplayName = connection.DisplayName;
        existing.UpdatedAt = now;
        existing.RefreshInProgressUntil = connection.RefreshInProgressUntil;
        db.SaveChanges();
        CachePut(ToModel(existing));
    }

    public ProjectorConnection? Get(string connectionId)
    {
        var cacheKey = $"conn:{connectionId}";
        if (_cache.TryGetValue(cacheKey, out ProjectorConnection? cached) && cached is not null)
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.Connections.AsNoTracking()
            .FirstOrDefault(c => c.ConnectionId == connectionId && c.ConnectionStatus == "Active");
        if (entity is null)
        {
            return null;
        }

        var model = ToModel(entity);
        CachePut(model);
        return model;
    }

    public ProjectorConnection? GetByOwner(string tenantId, string entraObjectId, string projectorAccountCode)
    {
        var cacheKey = $"owner:{tenantId}:{entraObjectId}:{projectorAccountCode}";
        if (_cache.TryGetValue(cacheKey, out ProjectorConnection? cached) && cached is not null)
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.Connections.AsNoTracking().FirstOrDefault(c =>
            c.TenantId == tenantId
            && c.EntraObjectId == entraObjectId
            && c.ProjectorAccountCode == projectorAccountCode
            && c.ConnectionStatus == "Active")
            ?? db.Connections.AsNoTracking().FirstOrDefault(c =>
                c.TenantId == tenantId
                && c.EntraObjectId == entraObjectId
                && c.ProjectorAccountCode == projectorAccountCode);
        if (entity is null)
        {
            return null;
        }

        var model = ToModel(entity);
        CachePut(model);
        return model;
    }

    public bool Remove(string connectionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.Connections.FirstOrDefault(c => c.ConnectionId == connectionId);
        if (entity is null)
        {
            return false;
        }

        entity.ConnectionStatus = "Revoked";
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        _cache.Remove($"conn:{connectionId}");
        if (!string.IsNullOrEmpty(entity.TenantId))
        {
            _cache.Remove($"owner:{entity.TenantId}:{entity.EntraObjectId}:{entity.ProjectorAccountCode}");
        }

        return true;
    }

    private ProjectorConnection ToModel(ProjectorConnectionEntity entity) => new()
    {
        ConnectionId = entity.ConnectionId,
        TenantId = entity.TenantId,
        EntraObjectId = entity.EntraObjectId,
        ProjectorAccountCode = entity.ProjectorAccountCode,
        SessionTicket = _encryption.Decrypt(entity.EncryptedAccessToken),
        RefreshToken = _encryption.Decrypt(entity.EncryptedRefreshToken),
        ExpiresAt = entity.AccessTokenExpiresAt,
        GrantedScope = entity.GrantedScopes,
        RestServiceAuthority = entity.RestServiceAuthority,
        SoapServiceAuthority = entity.SoapServiceAuthority,
        DisplayName = entity.DisplayName,
        ConnectionStatus = entity.ConnectionStatus,
        EncryptionKeyVersion = entity.EncryptionKeyVersion,
        RowVersion = entity.RowVersion,
        RefreshInProgressUntil = entity.RefreshInProgressUntil
    };

    private void CachePut(ProjectorConnection connection)
    {
        var ttl = connection.ExpiresAt - DateTimeOffset.UtcNow;
        if (ttl < TimeSpan.FromMinutes(1))
        {
            ttl = TimeSpan.FromMinutes(1);
        }

        if (ttl > TimeSpan.FromMinutes(30))
        {
            ttl = TimeSpan.FromMinutes(30);
        }

        var opts = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        _cache.Set($"conn:{connection.ConnectionId}", connection, opts);
        if (!string.IsNullOrEmpty(connection.TenantId) && !string.IsNullOrEmpty(connection.EntraObjectId))
        {
            _cache.Set(
                $"owner:{connection.TenantId}:{connection.EntraObjectId}:{connection.ProjectorAccountCode}",
                connection,
                opts);
        }
    }
}

public sealed class SqlPendingAuthorizationStore : IPendingAuthorizationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public SqlPendingAuthorizationStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Save(PendingAuthorization pending) => Upsert(pending.Code, pending);

    public PendingAuthorization? Peek(string code) => PeekKey(code);

    public PendingAuthorization? Take(string code) => TakeKey(code);

    public void SaveByState(string state, PendingProjectorLogin login) =>
        Upsert($"state:{state}", new PendingAuthorization
        {
            Code = $"state:{state}",
            ConnectionId = login.ConnectionId,
            ClientRedirectUri = login.ClientRedirectUri,
            ClientState = login.ClientState,
            ClientCodeChallenge = login.ClientCodeChallenge,
            ClientId = login.ClientId,
            ProjectorCodeVerifier = login.ProjectorCodeVerifier,
            EntraNonce = login.EntraNonce,
            TenantId = login.TenantId,
            EntraObjectId = login.EntraObjectId,
            ProjectorAccountCode = login.ProjectorAccountCode,
            ExpiresAt = login.ExpiresAt,
            Kind = PendingKind.ProjectorLogin
        });

    public PendingAuthorization? TakeByState(string state) => TakeKey($"state:{state}");

    public void SaveFlow(string flowId, PendingAuthorization pending) => Upsert($"flow:{flowId}", pending);

    public PendingAuthorization? GetFlow(string flowId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.PendingAuthorizations.AsNoTracking().FirstOrDefault(p => p.Key == $"flow:{flowId}");
        if (entity is null || entity.ConsumedAt is not null || entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return JsonSerializer.Deserialize<PendingAuthorization>(entity.PayloadJson, JsonOpts);
    }

    public void UpdateFlow(string flowId, PendingAuthorization pending) => Upsert($"flow:{flowId}", pending);

    public PendingAuthorization? TakeFlow(string flowId) => TakeKey($"flow:{flowId}");

    private void Upsert(string key, PendingAuthorization pending)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.PendingAuthorizations.FirstOrDefault(p => p.Key == key);
        if (entity is null)
        {
            entity = new PendingAuthorizationEntity { Key = key };
            db.PendingAuthorizations.Add(entity);
        }

        entity.PayloadJson = JsonSerializer.Serialize(pending, JsonOpts);
        entity.ExpiresAt = pending.ExpiresAt;
        entity.ConsumedAt = null;
        db.SaveChanges();
    }

    private PendingAuthorization? PeekKey(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.PendingAuthorizations.AsNoTracking().FirstOrDefault(p => p.Key == key);
        if (entity is null || entity.ConsumedAt is not null || entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return JsonSerializer.Deserialize<PendingAuthorization>(entity.PayloadJson, JsonOpts);
    }

    private PendingAuthorization? TakeKey(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectorTokenDbContext>();
        var entity = db.PendingAuthorizations.FirstOrDefault(p => p.Key == key);
        if (entity is null || entity.ConsumedAt is not null || entity.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        entity.ConsumedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return JsonSerializer.Deserialize<PendingAuthorization>(entity.PayloadJson, JsonOpts);
    }
}
