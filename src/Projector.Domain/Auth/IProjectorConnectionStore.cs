namespace Projector.Domain.Auth;

public interface IProjectorConnectionStore
{
    void Save(ProjectorConnection connection);

    ProjectorConnection? Get(string connectionId);

    ProjectorConnection? GetByOwner(string tenantId, string entraObjectId, string projectorAccountCode);

    bool Remove(string connectionId);
}
