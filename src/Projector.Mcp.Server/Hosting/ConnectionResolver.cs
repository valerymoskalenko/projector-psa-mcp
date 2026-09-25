using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// Resolves the Projector connection for HTTP (JWT tid/oid/connection_id) or CLI/stdio (DPAPI cache).
/// </summary>
public sealed class ConnectionResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILocalOAuthSessionStore _localStore;
    private readonly IProjectorConnectionStore _connectionStore;
    private readonly IOptions<ProjectorOptions> _options;

    public ConnectionResolver(
        IHttpContextAccessor httpContextAccessor,
        ILocalOAuthSessionStore localStore,
        IProjectorConnectionStore connectionStore,
        IOptions<ProjectorOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _localStore = localStore;
        _connectionStore = connectionStore;
        _options = options;
    }

    public Task<string> RequireConnectionIdAsync(CancellationToken cancellationToken = default)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var tid = user.FindFirstValue("tid");
            var oid = user.FindFirstValue("oid");
            var account = user.FindFirstValue("projector_account") ?? _options.Value.AccountCode;
            if (!string.IsNullOrWhiteSpace(tid) && !string.IsNullOrWhiteSpace(oid))
            {
                var byOwner = _connectionStore.GetByOwner(tid, oid, account);
                if (byOwner is not null)
                {
                    return Task.FromResult(byOwner.ConnectionId);
                }
            }

            var connectionId = user.FindFirstValue("connection_id")
                ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue("sub");
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                var existing = _connectionStore.Get(connectionId);
                if (existing is not null)
                {
                    return Task.FromResult(existing.ConnectionId);
                }
            }

            throw new ProjectorAuthorizationException(
                "No Projector connection for this caller. Reconnect via OAuth.");
        }

        // CLI / stdio: use DPAPI cache
        var opts = _options.Value;
        var requested = ProjectorScopes.Normalize(opts.RequestedScopes);
        var cached = _localStore.TryLoad(opts.AccountCode, requested);
        if (cached is null)
        {
            throw new ProjectorAuthorizationException(
                "No local OAuth session. Run: dotnet run --project src/Projector.Mcp.Server -- auth login");
        }

        _connectionStore.Save(cached);
        return Task.FromResult(cached.ConnectionId);
    }
}
