using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;
using Projector.Domain.Common;
using Projector.Domain.Exceptions;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Remaining agent tools (resources live in <see cref="ResourceTools"/>).
/// Tool names are verb_noun, registered exactly once, ≤30 chars (Agent 365 / MOS BYO registration).
/// M365 Copilot calls them with the first segment stripped (get_resource → resource);
/// <see cref="CopilotToolNameFilter"/> maps those calls back.
/// Returns <see cref="CallToolResult"/> so protocol failures set <c>isError</c>.
/// </summary>
[McpServerToolType]
public sealed class AgentTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectorToolService _tools;
    private readonly ConnectionResolver _connections;

    public AgentTools(ProjectorToolService tools, ConnectionResolver connections)
    {
        _tools = tools;
        _connections = connections;
    }

    [McpServerTool(Name = "list_timecards", Title = "List Projector timecards",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists many work timecard rows for one resource in a date range, with optional status and project_code filters. " +
        "Does not include time-off cards. " +
        ToolOutputSchemas.TimecardsSchemaHint + " " +
        "WhenNotToUse: Do not use for capacity or bookings; use check_availability or get_schedule. " +
        "Do not use for PTO cards; use list_time_off.")]
    public Task<CallToolResult> ListTimecards(
        [Description("ResourceReferenceSystemId")] string resource_id,
        [Description("Inclusive start date (yyyy-MM-dd)")] string start_date,
        [Description("Inclusive end date (yyyy-MM-dd)")] string end_date,
        [Description("Optional card status filter")] string? status = null,
        [Description("Optional project code filter")] string? project_code = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListTimecardsAsync(
            ct.ConnectionId, resource_id, start_date, end_date, status, project_code, ct.Token), cancellationToken);

    [McpServerTool(Name = "list_time_off", Title = "List Projector time-off cards",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists many PwsGetTimeCards time-off card rows for one resource and date range. " +
        ToolOutputSchemas.TimeOffSchemaHint + " " +
        "WhenNotToUse: Use list_upcoming_pto for a combined schedule+cards window. " +
        "Use list_holidays for company-wide location holiday calendars. " +
        "Do not use for work time entries; use list_timecards.")]
    public Task<CallToolResult> ListTimeOff(
        [Description("ResourceReferenceSystemId")] string resource_id,
        [Description("Inclusive start date")] string start_date,
        [Description("Inclusive end date")] string end_date,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListTimeOffAsync(
            ct.ConnectionId, resource_id, start_date, end_date, ct.Token), cancellationToken);

    [McpServerTool(Name = "get_schedule", Title = "Get Projector resource schedule",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Returns one resource’s schedule for a date window: working/utilization minutes and hours, holidays, PTO, " +
        "roles, bookings, plus daily/weekly capacity summaries. Cap is eight weeks. " +
        "WhenNotToUse: Do not use for multi-person availability comparisons. " +
        "Do not use for historical time entry totals; use list_timecards. " +
        "Do not use for a project team roster; use list_project_roles. " +
        "Do not use for project booked hours across teammates; use list_proj_bookings.")]
    public Task<CallToolResult> GetResourceSchedule(
        [Description("ResourceReferenceSystemId")] string resource_id,
        [Description("Inclusive start date")] string start_date,
        [Description("Inclusive end date")] string end_date,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.GetResourceScheduleAsync(
            ct.ConnectionId, resource_id, start_date, end_date, ct.Token), cancellationToken);

    [McpServerTool(Name = "check_availability", Title = "Check Projector availability",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Compares capacity and bookings for 1–20 people over a date window using utilization-basis minutes. " +
        "Optional resource_id is merged into people. " +
        "Weekly availability is always returned; set show_availability_days true to also include daily days. " +
        ToolOutputSchemas.AvailabilitySchemaHint + " " +
        "WhenNotToUse: Do not use for historical time entry totals; use list_timecards. " +
        "Do not use to list assigned projects only; use get_schedule.")]
    public Task<CallToolResult> CheckAvailability(
        [Description("Inclusive start date")] string start_date,
        [Description("Inclusive end date")] string end_date,
        [Description("Emails, display names, or system ids")] string[]? people = null,
        [Description("Optional ResourceReferenceSystemId alias for people")] string? resource_id = null,
        [Description("Required hours per week")] double? required_hours_per_week = null,
        [Description("Required minutes per week")] double? required_minutes_per_week = null,
        [Description("When true, include daily availability.days; default weekly only")] bool show_availability_days = false,
        CancellationToken cancellationToken = default) =>
        CheckAvailabilityCoreAsync(
            start_date, end_date, people, resource_id,
            required_hours_per_week, required_minutes_per_week, show_availability_days, cancellationToken);

    private Task<CallToolResult> CheckAvailabilityCoreAsync(
        string start_date,
        string end_date,
        string[]? people,
        string? resource_id,
        double? required_hours_per_week,
        double? required_minutes_per_week,
        bool show_availability_days,
        CancellationToken cancellationToken) =>
        InvokeAsync(ct =>
        {
            var list = new List<string>();
            if (people is { Length: > 0 })
            {
                list.AddRange(people.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(resource_id))
            {
                list.Add(resource_id.Trim());
            }

            list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return _tools.CheckAvailabilityAsync(
                ct.ConnectionId, list, start_date, end_date,
                required_hours_per_week, required_minutes_per_week, show_availability_days, ct.Token);
        }, cancellationToken);

    [McpServerTool(Name = "list_engagements", Title = "List Projector engagements",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists engagements with engagement manager plus nested projects. Does not return dollar budgets. " +
        ToolOutputSchemas.EngagementsSchemaHint + " " +
        "WhenNotToUse: Use get_engagement for one engagement’s billable/productive flags, cost center, and contracts.")]
    public Task<CallToolResult> ListEngagements(
        [Description("Fast server-side search")] string? query = null,
        [Description("Manager name/email substring")] string? manager_query = null,
        [Description("Any, Engagement, or Project")] string? manager_role = null,
        [Description("Include closed engagements")] bool? include_closed = null,
        [Description("Max rows 1-200")] int max_rows = 50,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListEngagementsAsync(
            ct.ConnectionId, query, manager_query, manager_role, include_closed, max_rows, ct.Token), cancellationToken);

    [McpServerTool(Name = "get_engagement", Title = "Get Projector engagement",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Gets one engagement by code with engagement manager, billable/productive flags, cost center, contracts, " +
        "nested projects, and planned hour/money budgets when the signed-in user's Projector permissions include them. " +
        "Money fields may be omitted — never treat missing money as $0. Does not compare actuals to budget. " +
        "WhenNotToUse: Do not use for resource schedules or timecards.")]
    public Task<CallToolResult> GetEngagement(
        [Description("EngagementCode")] string code,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.GetEngagementAsync(ct.ConnectionId, code, ct.Token), cancellationToken);

    [McpServerTool(Name = "list_upcoming_pto", Title = "List upcoming Projector PTO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists upcoming PTO for one resource by combining schedule holidays/PTO and time-off cards. " +
        "Each pto row includes minutes and hours (minutes / 60). " +
        ToolOutputSchemas.UpcomingPtoSchemaHint + " " +
        "WhenNotToUse: Do not use for company-wide holiday calendars; use list_holidays.")]
    public Task<CallToolResult> ListUpcomingPto(
        [Description("ResourceReferenceSystemId")] string resource_id,
        [Description("Optional start date")] string? start_date = null,
        [Description("Optional end date")] string? end_date = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListUpcomingPtoAsync(
            ct.ConnectionId, resource_id, start_date, end_date, ct.Token), cancellationToken);

    [McpServerTool(Name = "get_overview", Title = "Get Projector resource overview",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Returns a bounded bundle of resource profile, timecards, schedule/bookings/roles, and time off for one resource. " +
        "WhenNotToUse: Do not use for simple availability checks; use check_availability.")]
    public Task<CallToolResult> GetResourceOverview(
        [Description("ResourceReferenceSystemId")] string resource_id,
        [Description("Inclusive start date")] string start_date,
        [Description("Inclusive end date")] string end_date,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.GetResourceOverviewAsync(
            ct.ConnectionId, resource_id, start_date, end_date, ct.Token), cancellationToken);

    [McpServerTool(Name = "list_holidays", Title = "List Projector holiday calendars",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists company holiday calendars by location for an inclusive date range. " +
        "Returns compact calendars (location + holiday dates/names), not per-person rows. " +
        ToolOutputSchemas.HolidaysSchemaHint + " " +
        "WhenNotToUse: Do not use for one person's holidays, PTO, or bookings; " +
        "use get_schedule or list_upcoming_pto.")]
    public Task<CallToolResult> ListHolidays(
        [Description("Inclusive start date")] string start_date,
        [Description("Inclusive end date; defaults to start plus three months")] string? end_date = null,
        [Description("Optional LocationName substring")] string? location = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListHolidaysAsync(
            ct.ConnectionId, start_date, end_date, location, ct.Token), cancellationToken);

    [McpServerTool(Name = "list_project_roles", Title = "List Projector project roles",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists many booked/assigned project role rows for one or more project codes " +
        "(role name, resource id/name, optional email). Uses PwsGetProjectRoles Mode=A. " +
        "Does not return date-window booked hours. " +
        ToolOutputSchemas.ProjectRolesSchemaHint + " " +
        "WhenNotToUse: Do not use for October-style booked hours; use list_proj_bookings. " +
        "Do not use for one person's assigned projects; use get_schedule. " +
        "Do not use for capacity; use check_availability.")]
    public Task<CallToolResult> ListProjectRoles(
        [Description("One project code (e.g. P001234-001)")] string? project_code = null,
        [Description("1–100 project codes")] string[]? project_codes = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListProjectRolesAsync(
            ct.ConnectionId, NormalizeProjectCodes(project_code, project_codes), ct.Token), cancellationToken);

    [McpServerTool(Name = "list_proj_bookings", Title = "List Projector project bookings",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists many booked-hour rows for one or more project codes in an inclusive date window " +
        "(role, resource, date, scheduled minutes/hours). Uses PwsGetResourceSchedulingRoleData mode A; " +
        "one SOAP call per project with bounded concurrency. Zero-hour buckets are omitted. " +
        "Accepts many project_codes in one MCP call. " +
        ToolOutputSchemas.ProjectBookingsSchemaHint + " " +
        "WhenNotToUse: Do not use for assignment roster without hours; use list_project_roles. " +
        "Do not use for one person's load; use get_schedule. " +
        "Do not use for actual timecards; use list_timecards.")]
    public Task<CallToolResult> ListProjectBookings(
        [Description("Inclusive start date (yyyy-MM-dd)")] string start_date,
        [Description("Inclusive end date (yyyy-MM-dd)")] string end_date,
        [Description("One project code")] string? project_code = null,
        [Description("1–100 project codes")] string[]? project_codes = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _tools.ListProjectBookingsAsync(
            ct.ConnectionId, NormalizeProjectCodes(project_code, project_codes),
            start_date, end_date, ct.Token), cancellationToken);

    private static IReadOnlyList<string> NormalizeProjectCodes(string? projectCode, string[]? projectCodes)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectCode))
        {
            list.Add(projectCode.Trim());
        }

        if (projectCodes is not null)
        {
            foreach (var code in projectCodes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                {
                    list.Add(code.Trim());
                }
            }
        }

        list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            throw new ProjectorApiException(
                "Provide project_code or project_codes (1–100).",
                "InvalidArgument");
        }

        return list;
    }

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
            return ToError(ex);
        }
    }

    internal static CallToolResult ToError(Exception ex)
    {
        var (code, message) = ex switch
        {
            ProjectorAuthorizationException auth => ("authorization_error", SecretRedactor.Redact(auth.Message)),
            ProjectorApiException api => (api.ErrorCode ?? "projector_error", SecretRedactor.Redact(api.Message)),
            FluentValidation.ValidationException validation => ("validation_error", SecretRedactor.Redact(validation.Message)),
            ArgumentException arg => ("invalid_argument", SecretRedactor.Redact(arg.Message)),
            OperationCanceledException => ("cancelled", "The operation was cancelled."),
            _ => ("error", "An unexpected error occurred.")
        };

        var body = JsonSerializer.Serialize(new { error = code, message }, JsonOptions);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = body }]
        };
    }
}
