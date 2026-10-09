using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Time entry on the signed-in user's own time sheet: two lookups and the only write tool.
/// No tool here takes a resource: Projector applies every call to the caller.
/// save_timecard creates Draft cards or updates the caller's Draft/Rejected cards; it never submits.
/// </summary>
[McpServerToolType]
public sealed class TimeEntryTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeEntryToolService _timeEntry;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<TimeEntryTools> _logger;

    public TimeEntryTools(TimeEntryToolService timeEntry, ConnectionResolver connections, ILogger<TimeEntryTools> logger)
    {
        _timeEntry = timeEntry;
        _connections = connections;
        _logger = logger;
    }

    [McpServerTool(Name = "list_time_projects", Title = "List projects I can enter time on",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists many projects the signed-in user can enter time on for one work_date, with the user's roles on each. " +
        "Always the signed-in user's own time sheet. Optional query matches whole words or word starts of the project, " +
        "engagement or client; page with offset. By default only chargeable projects (where the user has a role) are " +
        "listed; not_chargeable_hidden counts the others, which chargeable_only = false shows with chargeable = false. " +
        "Each project shows the user's use in the last 30 days (last_used, hours_last_30d, recent_tasks with task path " +
        "and WBS), most recently used first; query also matches those recent task names (matched_tasks), so a task such " +
        "as a presale opportunity finds its project. " +
        ToolOutputSchemas.TimeProjectsSchemaHint + " " +
        "WhenNotToUse: Do not use to browse engagements or a manager's projects; use list_engagements. " +
        "Do not use for who is staffed on a project; use list_project_roles.")]
    public Task<CallToolResult> ListTimeProjects(
        [Description("Work date (yyyy-MM-dd) the time is for")] string work_date,
        [Description("Optional text to match project, engagement or client")] string? query = null,
        [Description("Maximum projects to return (1-200)")] int max_rows = 50,
        [Description("Rows to skip; use next_offset from the previous page")] int offset = 0,
        [Description("true (default): only projects where you have a role and can post time; false: also the others")] bool chargeable_only = true,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _timeEntry.ListTimeProjectsAsync(ct.ConnectionId, work_date, query, max_rows, ct.Token, offset, chargeable_only),
            cancellationToken);

    [McpServerTool(Name = "get_timecard_options", Title = "Get time entry options for a project",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Gets one project's time-entry options for the signed-in user on one work_date: only tasks open for time " +
        "(name, full task_path with parent tasks, WBS code, default_rate_type), the user's roles, and entry rules " +
        "(time increment, location, UDFs). save_timecard always uses the task's default_rate_type, so don't offer rate " +
        "type choices to the user. Task names repeat under different parents, so use query (part of a task name, WBS code or parent name) to find the " +
        "right one; results are paged (max_tasks, offset). Call before save_timecard. " +
        ToolOutputSchemas.TimecardOptionsSchemaHint + " " +
        "WhenNotToUse: Do not use to find the user's projects; use list_time_projects. " +
        "Do not use for a project's staffing roster; use list_project_roles.")]
    public Task<CallToolResult> GetTimecardOptions(
        [Description("Project code, e.g. P001234-001 (from list_time_projects)")] string project_code,
        [Description("Work date (yyyy-MM-dd) the time is for")] string work_date,
        [Description("Optional text to match task name, task path (parent names) or WBS code, e.g. a story number or 3.2")] string? query = null,
        [Description("Maximum tasks to return (1-200)")] int max_tasks = TimeEntryToolService.DefaultMaxTasks,
        [Description("Tasks to skip; use tasks_next_offset from the previous page")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _timeEntry.GetTimecardOptionsAsync(
                ct.ConnectionId, project_code, work_date, ct.Token, query, max_tasks, offset),
            cancellationToken);

    [McpServerTool(Name = "save_timecard", Title = "Save my Projector time card (draft)",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Saves 1–20 work time cards on the signed-in user's own time sheet in one call: each card is created as Draft, " +
        "or updates one of the user's own Draft or Rejected cards when its timecard_uid is given (from list_timecards). " +
        "Never submits, approves or deletes; the user submits in Projector. To remove a card, tell the user to " +
        "delete it in Projector: this tool can't delete, and a card can't be saved with 0 hours. Before calling, show the user every card " +
        "(date, hours, project, task path, role, narrative) and get one explicit confirmation; then send all approved " +
        "cards in one call. Find the values with list_time_projects, then get_timecard_options; task accepts the WBS " +
        "code (preferred), the UID, the task_path (or its end) or a unique name; role accepts the UID or the exact name. " +
        "The rate type is not a parameter: the server always uses the task's default rate type. On an update send the " +
        "full card. Every card is checked first: invalid cards are reported and not sent, valid ones are saved one by " +
        "one. dry_run defaults to true: it checks everything and shows the day totals without saving; send the approved " +
        "cards again with dry_run = false to save. Relay each card's status and " +
        "warnings (e.g. a likely duplicate, a weekend, holiday or PTO date, more hours than the day expects, a date " +
        "after the project end) to the user: the cards are saved anyway, so ask whether they should stay. " +
        ToolOutputSchemas.SaveTimecardSchemaHint + " " +
        "WhenNotToUse: Do not use to read cards; use list_timecards. Do not use for time off; it writes work time only.")]
    public Task<CallToolResult> SaveTimecard(
        [Description("The cards to save (1–20), in the order the user approved them")] SaveTimecardCard[] cards,
        [Description("Default true: check only, nothing is saved. Pass false to save, only after the user's explicit OK (true shows every card's status and the day totals)")] bool dry_run = true,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _timeEntry.SaveTimecardsAsync(
                ct.ConnectionId,
                (cards ?? []).Select(c => c.ToInput()).ToList(),
                dry_run,
                ct.Token),
            cancellationToken);

    private async Task<CallToolResult> InvokeAsync(
        Func<(string ConnectionId, CancellationToken Token), Task<object>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionId = await _connections.RequireConnectionIdAsync(cancellationToken);
            var payload = await action((connectionId, cancellationToken));
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
                StructuredContent = JsonSerializer.SerializeToElement(payload, JsonOptions)
            };
        }
        catch (Exception ex)
        {
            return AgentTools.ToError(ex, _logger);
        }
    }
}
