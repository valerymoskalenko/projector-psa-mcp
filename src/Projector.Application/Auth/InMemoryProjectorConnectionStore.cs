using System.Collections.Concurrent;
using Projector.Domain.Auth;

namespace Projector.Application.Auth;

public sealed class InMemoryProjectorConnectionStore : IProjectorConnectionStore
{
    private readonly ConcurrentDictionary<string, ProjectorConnection> _store =
        new(StringComparer.Ordinal);

    public void Save(ProjectorConnection connection) =>
        _store[connection.ConnectionId] = connection;

    public ProjectorConnection? Get(string connectionId) =>
        _store.TryGetValue(connectionId, out var connection) ? connection : null;

    public ProjectorConnection? GetByOwner(string tenantId, string entraObjectId, string projectorAccountCode)
    {
        return _store.Values.FirstOrDefault(c =>
            string.Equals(c.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.EntraObjectId, entraObjectId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ProjectorAccountCode, projectorAccountCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ConnectionStatus, "Active", StringComparison.OrdinalIgnoreCase));
    }

    public bool Remove(string connectionId) => _store.TryRemove(connectionId, out _);
}
