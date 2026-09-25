using System.Collections.Concurrent;
using Projector.Domain.Auth;

namespace Projector.Application.Auth;

public sealed class InMemoryPendingAuthorizationStore : IPendingAuthorizationStore
{
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new(StringComparer.Ordinal);

    public void Save(PendingAuthorization pending) => _pending[pending.Code] = pending;

    public PendingAuthorization? Peek(string code) =>
        _pending.TryGetValue(code, out var pending) && !pending.IsExpired ? pending : null;

    public PendingAuthorization? Take(string code) =>
        _pending.TryRemove(code, out var pending) ? pending : null;

    public void SaveByState(string state, PendingProjectorLogin login) =>
        _pending[$"state:{state}"] = new PendingAuthorization
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
        };

    public PendingAuthorization? TakeByState(string state) => Take($"state:{state}");

    public void SaveFlow(string flowId, PendingAuthorization pending) =>
        _pending[$"flow:{flowId}"] = pending;

    public PendingAuthorization? GetFlow(string flowId) =>
        _pending.TryGetValue($"flow:{flowId}", out var pending) ? pending : null;

    public void UpdateFlow(string flowId, PendingAuthorization pending) =>
        _pending[$"flow:{flowId}"] = pending;

    public PendingAuthorization? TakeFlow(string flowId) =>
        _pending.TryRemove($"flow:{flowId}", out var pending) ? pending : null;
}
