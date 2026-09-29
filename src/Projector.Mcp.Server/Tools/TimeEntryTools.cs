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
        "(time increment, location, UDFs). Rate types are listed for information only (the ones most tasks allow once " +
        "at the top; a task lists its own only when they differ): save_timecard always uses the task's " +
        "default_rate_type, so don't offer rate type choices to the user. Task names repeat under different parents, so use query (part of a task name, WBS code or parent name) to find the " +
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
        "Creates one work time card as Draft on the signed-in user's own time sheet, or updates one of the user's own " +
        "Draft or Rejected cards when timecard_uid is given (from list_timecards). Never submits, approves or deletes; " +
        "the user submits in Projector. Before calling, show the user the date, hours, project, task, role " +
        "and narrative and get explicit confirmation. Find the values with list_time_projects, then get_timecard_options; " +
        "task accepts the UID, the full task_path, the WBS code or a unique name; role accepts the UID or the exact name. " +
        "The rate type is not a parameter: the server always uses the task's default rate type (on update too; moving a " +
        "card to another task gives it that task's default). On an update send the full card, not just the changes. " +
        "The result includes the day's total hours and warns about a likely duplicate card (same date, project and task, similar narrative); relay warnings to the user. " +
        ToolOutputSchemas.SaveTimecardSchemaHint + " " +
        "WhenNotToUse: Do not use to read cards; use list_timecards. Do not use for time off; it writes work time only.")]
    public Task<CallToolResult> SaveTimecard(
        [Description("Work date (yyyy-MM-dd). On update, the card's current work date.")] string work_date,
        [Description("Hours worked, e.g. 1.5. More than 0, at most 24, in the account's time increment.")] double hours,
        [Description("Project code, e.g. P001234-001")] string project_code,
        [Description("Task UID, task_path, WBS code or unique task name (get_timecard_options)")] string task,
        [Description("Role UID or exact role name (get_timecard_options)")] string role,
        [Description("What was done; required, at most 1000 characters")] string narrative,
        [Description("Only to update: the card's timecardUid from list_timecards. Omit to create a new Draft card.")] string? timecard_uid = null,
        [Description("Location name; only when get_timecard_options says location_required")] string? location = null,
        [Description("Text for UDF 1; only when get_timecard_options lists rules.udf1")] string? udf1 = null,
        [Description("Text for UDF 2; only when get_timecard_options lists rules.udf2")] string? udf2 = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _timeEntry.SaveTimecardAsync(
                ct.ConnectionId,
                new SaveTimecardInput(work_date, hours, project_code, task, role, narrative,
                    timecard_uid, location, udf1, udf2),
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
