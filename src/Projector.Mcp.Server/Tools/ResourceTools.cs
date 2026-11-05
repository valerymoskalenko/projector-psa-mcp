using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Resources;
using Projector.Application.Tools;
using Projector.Contracts.Resources;
using Projector.Domain.Exceptions;
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
    private readonly ILogger<ResourceTools> _logger;

    public ResourceTools(ResourceService resources, ConnectionResolver connections, ILogger<ResourceTools> logger)
    {
        _resources = resources;
        _connections = connections;
        _logger = logger;
    }

    [McpServerTool(Name = "list_resources", Title = "List Projector resources",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Searches Projector resource summaries by optional query string (people catalog). " +
        "Returns truncated lists. Does not return schedules or timecards. " +
        "People of one team: pass cost_center and/or department (text contained in the name, case-insensitive); " +
        "each row then has cost_center, department and title, and max_rows goes up to 500. " +
        "Before a report or check about many people (missing time, hours, bookings) where the user named no " +
        "cost center, department or list of people, do not assume everyone: call with list_groups=true and ask " +
        "the user which cost centers or departments to include, showing the options with their head counts. " +
        "cost_center, department and list_groups need the Projector permission Export Data. " +
        ToolOutputSchemas.ResourcesListSchemaHint + " " +
        "WhenNotToUse: Do not use to resolve a single person by email/resource_id/full name; prefer get_resource.")]
    public Task<CallToolResult> ListResources(
        [Description("Optional search string matching display name, email, or reference system id")] string? query = null,
        [Description("Include inactive resources")] bool include_inactive = false,
        [Description("Maximum rows to return (1-100; up to 500 with cost_center or department)")] int max_rows = 50,
        [Description("Only people whose cost center contains this text, e.g. Professional Services")] string? cost_center = null,
        [Description("Only people whose department contains this text")] string? department = null,
        [Description("Return groups: the cost centers and departments with head counts, to ask the user which to include")] bool list_groups = false,
        CancellationToken cancellationToken = default) =>
        ListResourcesCoreAsync(query, include_inactive, max_rows, cost_center, department, list_groups, cancellationToken);

    private async Task<CallToolResult> ListResourcesCoreAsync(
        string? query,
        bool includeInactive,
        int maxRows,
        string? costCenter,
        string? department,
        bool listGroups,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
            var response = await _resources.ListAsync(
                connectionId,
                new ListResourcesRequest(query, includeInactive, maxRows, costCenter, department, listGroups),
                cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return AgentTools.ToError(ex, _logger);
        }
    }

    [McpServerTool(Name = "get_resource", Title = "Get Projector resource",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Gets one Projector resource (a person's profile). Provide one of: resource_id (preferred/fastest), " +
        "full_name (exact display name), or email (slower — list search then detail). " +
        "There is no lookup of the signed-in user: their own time cards, schedule and PTO need no resource id " +
        "(omit resource_id on list_timecards, get_schedule, list_upcoming_pto). " +
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
            var candidates = LookupCandidates(resourceId, fullName, email);
            var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
            for (var i = 0; ; i++)
            {
                try
                {
                    var response = await _resources.GetAsync(
                        connectionId,
                        new GetResourceRequest(candidates[i], includeHistory, includeUdfs),
                        cancellationToken);
                    return Ok(response);
                }
                catch (ProjectorApiException ex) when (i < candidates.Count - 1 && IsNotFound(ex))
                {
                    // Several identifiers were sent and this one found nobody: try the next.
                }
            }
        }
        catch (Exception ex)
        {
            return AgentTools.ToError(ex, _logger);
        }
    }

    private static bool IsNotFound(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "AtLeastOneItemNotFound", StringComparison.OrdinalIgnoreCase);

    private static CallToolResult Ok(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
    }

    /// <summary>
    /// Projector has no "current user" lookup (PwsGetResource needs an identity, and the caller's own schedule and
    /// time cards carry no resource id), so a call without a person, or for "me", is answered with what works instead.
    /// </summary>
    internal const string NoPersonMessage =
        "get_resource needs one of resource_id, full_name or email: Projector cannot look up the signed-in user's own " +
        "profile without one. For the signed-in user's own time cards, schedule, PTO or time projects no lookup is " +
        "needed: call list_timecards, get_schedule, list_upcoming_pto or list_time_projects without resource_id. " +
        "For their profile, pass their e-mail or full name.";

    /// <summary>
    /// The identifiers to try, fastest first (resource_id, full_name, email). Clients sometimes send two of them;
    /// the next one is tried only when the previous one finds nobody.
    /// </summary>
    internal static IReadOnlyList<string> LookupCandidates(string? resourceId, string? fullName, string? email)
    {
        var provided = new[] { resourceId, fullName, email }
            .Where(s => !string.IsNullOrWhiteSpace(s)
                && !string.Equals(s.Trim(), ProjectorToolService.SignedInUser, StringComparison.OrdinalIgnoreCase))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (provided.Count == 0)
        {
            throw new ArgumentException(NoPersonMessage);
        }

        return provided;
    }
}
