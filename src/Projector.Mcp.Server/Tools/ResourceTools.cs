using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Resources;
using Projector.Contracts.Resources;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ResourceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ResourceService _resources;
    private readonly ConnectionResolver _connections;

    public ResourceTools(ResourceService resources, ConnectionResolver connections)
    {
        _resources = resources;
        _connections = connections;
    }

    [McpServerTool(Name = "list_resources", Title = "List Projector resources",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Searches Projector resource summaries by optional query string (people catalog). " +
        "Returns truncated lists. Does not return schedules or timecards. " +
        ToolOutputSchemas.ResourcesListSchemaHint + " " +
        "WhenNotToUse: Do not use to resolve a single person by email/resource_id/full name; prefer get_resource.")]
    public Task<CallToolResult> ListResources(
        [Description("Optional search string matching display name, email, or reference system id")] string? query = null,
        [Description("Include inactive resources")] bool include_inactive = false,
        [Description("Maximum rows to return (1-100)")] int max_rows = 50,
        CancellationToken cancellationToken = default) =>
        ListResourcesCoreAsync(query, include_inactive, max_rows, cancellationToken);

    private async Task<CallToolResult> ListResourcesCoreAsync(
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
            var response = await _resources.ListAsync(
                connectionId,
                new ListResourcesRequest(query, includeInactive, maxRows),
                cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return AgentTools.ToError(ex);
        }
    }

    [McpServerTool(Name = "get_resource", Title = "Get Projector resource",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Gets one Projector resource. Provide exactly one of: resource_id (preferred/fastest), " +
        "full_name (exact display name), or email (slower — list search then detail). " +
        ToolOutputSchemas.ResourceGetSchemaHint + " " +
        "WhenNotToUse: Do not use for timecards, schedules, or multi-person search lists.")]
    public Task<CallToolResult> GetResource(
        [Description("ResourceReferenceSystemId or ResourceUid. Do not pass email here.")] string? resource_id = null,
        [Description("Exact ResourceDisplayName (e.g. Jane Doe). Prefer over email.")] string? full_name = null,
        [Description("Person email. Slower than resource_id/full_name.")] string? email = null,
        [Description("Include resource history entries")] bool include_history = false,
        [Description("Include user-defined fields")] bool include_udfs = true,
        CancellationToken cancellationToken = default) =>
        GetResourceCoreAsync(resource_id, full_name, email, include_history, include_udfs, cancellationToken);

    private async Task<CallToolResult> GetResourceCoreAsync(
        string? resourceId,
        string? fullName,
        string? email,
        bool includeHistory,
        bool includeUdfs,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = PickOne(resourceId, fullName, email);
            var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
            var response = await _resources.GetAsync(
                connectionId,
                new GetResourceRequest(id, includeHistory, includeUdfs),
                cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return AgentTools.ToError(ex);
        }
    }

    private static CallToolResult Ok(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
    }

    private static string PickOne(string? resourceId, string? fullName, string? email)
    {
        var provided = new[] { resourceId, fullName, email }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        if (provided.Count != 1)
        {
            throw new ArgumentException("Provide exactly one of resource_id, full_name, or email.");
        }

        return provided[0]!;
    }
}
