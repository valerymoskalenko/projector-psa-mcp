namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// The server's name and version: MCP serverInfo (stdio and HTTP), <c>GET /</c> and <c>GET /health</c>.
/// tools.json carries the same version (a test keeps the two equal).
/// </summary>
public static class ServerVersion
{
    public const string Name = "Projector PSA MCP Server";

    public const string Current = "0.7.1";
}
