namespace Projector.Domain.Auth;

public static class ProjectorScopes
{
    /// <summary>
    /// Projector special scope: session gets the signed-in user's full permissions.
    /// Cannot be combined with any other Projector scope tags.
    /// </summary>
    public const string AllowFullPermissions = "allowFullPermissions";

    /// <summary>Cost-center permission: View Resources (catalog metadata / least-privilege docs).</summary>
    public const string BrowseResources = "browseResources";

    public const string EnterTime = "enterTime";

    public const string ScheduleResources = "scheduleResources";

    public const string ScheduleEngagements = "scheduleEngagements";

    public const string ViewProjects = "viewProjects";

    public const string ExportData = "V:exportData";

    /// <summary>MCP Bearer scope for this server (not a Projector tag).</summary>
    public const string McpTools = "mcp:tools";

    /// <summary>Legacy alias kept for tests / older JWT samples.</summary>
    public const string McpResourcesRead = "mcp:resources.read";

    public static string Normalize(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return AllowFullPermissions;
        }

        var parts = scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(' ', parts);
    }

    public static string CacheKey(string accountCode, string? scope) =>
        $"{accountCode.Trim().ToLowerInvariant()}|{Normalize(scope)}";
}
