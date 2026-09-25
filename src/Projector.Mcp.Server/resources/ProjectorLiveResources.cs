using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Resources;
using Projector.Application.Tools;
using Projector.Contracts.Resources;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Resources;

/// <summary>
/// Live projector:// resources (OAuth session) — parity with Get-ProjectorMcpResource.ps1.
/// Static catalogs remain on <see cref="ProjectorReferenceResources"/>.
/// </summary>
[McpServerResourceType]
public sealed class ProjectorLiveResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ResourceService _resources;
    private readonly ProjectorToolService _tools;
    private readonly ConnectionResolver _connections;

    public ProjectorLiveResources(
        ResourceService resources,
        ProjectorToolService tools,
        ConnectionResolver connections)
    {
        _resources = resources;
        _tools = tools;
        _connections = connections;
    }

    [McpServerResource(UriTemplate = "projector://resources", Name = "projector_resources", MimeType = "application/json")]
    [Description("Live Projector resource list page (searchCoverage + resource_links).")]
    public async Task<TextResourceContents> ListResources(
        [Description("Max rows")] int max_rows = 50,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var response = await _resources.ListAsync(
            connectionId,
            new ListResourcesRequest(null, false, max_rows),
            cancellationToken);
        return Json("projector://resources", new
        {
            uri = "projector://resources",
            response.Resources,
            response.Count,
            has_more = response.HasMore,
            response.SearchCoverage,
            resource_links = response.ResourceLinks
        });
    }

    [McpServerResource(UriTemplate = "projector://resources/{id}", Name = "projector_resource", MimeType = "application/json")]
    [Description("Live Projector resource detail with resource_links to history and udfs.")]
    public async Task<TextResourceContents> GetResource(
        string id,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var response = await _resources.GetAsync(
            connectionId,
            new GetResourceRequest(Uri.UnescapeDataString(id), IncludeHistory: false, IncludeUdfs: true),
            cancellationToken);
        return Json(response.Uri, response);
    }

    [McpServerResource(UriTemplate = "projector://resources/{id}/history", Name = "projector_resource_history", MimeType = "application/json")]
    [Description("Live Projector resource history entries.")]
    public async Task<TextResourceContents> GetResourceHistory(
        string id,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var response = await _resources.GetAsync(
            connectionId,
            new GetResourceRequest(Uri.UnescapeDataString(id), IncludeHistory: true, IncludeUdfs: false),
            cancellationToken);
        var uri = $"projector://resources/{response.Resource.ResourceReferenceSystemId}/history";
        return Json(uri, new { uri, history = response.Resource.History });
    }

    [McpServerResource(UriTemplate = "projector://resources/{id}/udfs", Name = "projector_resource_udfs", MimeType = "application/json")]
    [Description("Live Projector resource UDF subset.")]
    public async Task<TextResourceContents> GetResourceUdfs(
        string id,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var response = await _resources.GetAsync(
            connectionId,
            new GetResourceRequest(Uri.UnescapeDataString(id), IncludeHistory: false, IncludeUdfs: true),
            cancellationToken);
        var uri = $"projector://resources/{response.Resource.ResourceReferenceSystemId}/udfs";
        var udfs = FilterUdfSubset(response.Resource.Udfs);
        return Json(uri, new { uri, udfs });
    }

    [McpServerResource(UriTemplate = "projector://engagements", Name = "projector_engagements", MimeType = "application/json")]
    [Description("Live Projector engagement list with managers and resource_links.")]
    public async Task<TextResourceContents> ListEngagements(
        [Description("Max rows")] int max_rows = 50,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var payload = await _tools.ListEngagementsAsync(
            connectionId, query: null, managerQuery: null, managerRole: null,
            includeClosed: true, maxRows: max_rows, cancellationToken);
        return Json("projector://engagements", payload);
    }

    [McpServerResource(UriTemplate = "projector://engagements/{code}", Name = "projector_engagement", MimeType = "application/json")]
    [Description("Live Projector engagement detail.")]
    public async Task<TextResourceContents> GetEngagement(
        string code,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var payload = await _tools.GetEngagementAsync(
            connectionId, Uri.UnescapeDataString(code), cancellationToken);
        return Json($"projector://engagements/{Uri.UnescapeDataString(code)}", payload);
    }

    [McpServerResource(UriTemplate = "projector://engagements/{code}/projects", Name = "projector_engagement_projects", MimeType = "application/json")]
    [Description("Nested projects for one engagement.")]
    public async Task<TextResourceContents> GetEngagementProjects(
        string code,
        CancellationToken cancellationToken = default)
    {
        var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
        var decoded = Uri.UnescapeDataString(code);
        var payload = await _tools.GetEngagementAsync(connectionId, decoded, cancellationToken);
        var json = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var projects = json.TryGetProperty("engagement", out var eng) && eng.TryGetProperty("projects", out var p)
            ? p
            : json.TryGetProperty("Projects", out var p2) ? p2 : default;
        var uri = $"projector://engagements/{decoded}/projects";
        return Json(uri, new { uri, projects });
    }

    private static TextResourceContents Json(string uri, object payload) =>
        new()
        {
            Uri = uri,
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(payload, JsonOptions)
        };

    private static IReadOnlyList<object> FilterUdfSubset(IReadOnlyList<ResourceUdfDto>? udfs)
    {
        if (udfs is null || udfs.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Resource Department",
            "Resource Division",
            "Team Manager",
            "Resource Technology",
            "Technology",
            "Service Line"
        };

        return udfs
            .Where(u => u.Name is not null && wanted.Contains(u.Name))
            .Select(u => (object)new { name = u.Name, value = u.Value })
            .ToList();
    }
}
