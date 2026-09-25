namespace Projector.Domain.Auth;

/// <summary>
/// Persistent local OAuth session cache (DPAPI on Windows).
/// Used by CLI, stdio, and as a seed for HTTP broker connections.
/// </summary>
public interface ILocalOAuthSessionStore
{
    ProjectorConnection? TryLoad(string accountCode, string? requestedScope = null);

    void Save(ProjectorConnection connection, string accountCode, string requestedScope);

    bool Clear(string accountCode, string? requestedScope = null);

    string GetCacheDirectory();
}
