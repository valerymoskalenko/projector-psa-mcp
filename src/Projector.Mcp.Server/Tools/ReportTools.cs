using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// get_report: large, cross-person results (saved reports, the Ginsu export, the project list, approved time cards)
/// in the signed-in user's own Projector session. Read-only: starting a report run or an export changes no business
/// data. Who sees the tool is a setting (<see cref="ReportAccessFilter"/>).
/// </summary>
[McpServerToolType]
public sealed class ReportTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ReportToolService _reports;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<ReportTools> _logger;

    public ReportTools(ReportToolService reports, ConnectionResolver connections, ILogger<ReportTools> logger)
    {
        _reports = reports;
        _connections = connections;
        _logger = logger;
    }

    [McpServerTool(Name = ReportAccessFilter.ToolName, Title = "Get a Projector report or export",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Returns large, cross-person results as rows, with the signed-in user's own Projector permissions. " +
        "Call it without arguments to list the datasets with their columns and an example, and the user's recent report runs. " +
        "Datasets: 'ginsu' = hours across people and projects for a date range (posted approved and unapproved hours, " +
        "planned booked hours, time off, revenue), grouped by the chosen columns and added up; " +
        "'projects' = every project with client, engagement, managers, dates and stage; " +
        "'time_cards' = approved time cards of many people, card by card with descriptions; " +
        "'report' = a report the user saved in Projector, by its web service code (latest run), its spec_uid (runs it now) " +
        "or an output_uid. " +
        "Use columns to choose what comes back and query to keep only matching rows. A large result comes in parts: " +
        "when has_more is true, call again with only the argument cursor set to the next_cursor value of the answer. " +
        "A run can take a while: status 'running' means call again the same way after a few seconds. " +
        ToolOutputSchemas.ReportSchemaHint + " " +
        "WhenNotToUse: Do not use for one person's time cards, schedule or PTO; use list_timecards, get_schedule, " +
        "list_upcoming_pto. Do not use for one engagement or one project's team; use get_engagement, list_project_roles.")]
    public Task<CallToolResult> GetReport(
        [Description("report, ginsu, projects or time_cards; omit to list the datasets and the user's recent report runs")] string? dataset = null,
        [Description("report: the web service code set on the report's Output tab in Projector (returns the latest run)")] string? code = null,
        [Description("report: the Report Spec UID (Additional Actions > Show Report Spec UID); runs the user's own report now")] string? spec_uid = null,
        [Description("report: the output UID of one finished run")] string? output_uid = null,
        [Description("ginsu, time_cards: inclusive start date (yyyy-MM-dd)")] string? start_date = null,
        [Description("ginsu, time_cards: inclusive end date (yyyy-MM-dd), at most 366 days after start_date")] string? end_date = null,
        [Description("ginsu: last day of actual (posted) hours; later days are planned hours. Default: today, or end_date when it is earlier")] string? cutoff_date = null,
        [Description("ginsu: period of each row: day, week, month (default), quarter, year or none")] string? bucket = null,
        [Description("ginsu: cost center number; only that cost center and those under it")] string? cost_center = null,
        [Description("ginsu: what cost_center filters: projects (default) or resources")] string? by = null,
        [Description("ginsu: only billable engagements")] bool billable_only = false,
        [Description("ginsu: include submitted, not yet approved time (default true)")] bool include_unapproved = true,
        [Description("ginsu: include time off and holidays")] bool include_time_off = false,
        [Description("projects: also list projects closed for time (default false)")] bool include_closed = false,
        [Description("Optional words every returned row must contain (whole words or word starts), e.g. a person or client name")] string? query = null,
        [Description("Optional columns to return; for ginsu they also group the rows. See the dataset list for the names")] string[]? columns = null,
        [Description("Rows per answer (1-500)")] int max_rows = ReportToolService.DefaultMaxRows,
        [Description("The next_cursor value of the previous answer; when given, every other argument is ignored")] string? cursor = null,
        [Description("Same as cursor (accepted because agents pass the output key name)")] string? next_cursor = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _reports.GetReportAsync(
            ct.ConnectionId,
            new ReportRequest
            {
                Dataset = dataset,
                Code = code,
                SpecUid = spec_uid,
                OutputUid = output_uid,
                StartDate = start_date,
                EndDate = end_date,
                CutoffDate = cutoff_date,
                Bucket = bucket,
                CostCenter = cost_center,
                By = by,
                BillableOnly = billable_only,
                IncludeUnapproved = include_unapproved,
                IncludeTimeOff = include_time_off,
                IncludeClosed = include_closed,
                Query = query,
                Columns = columns,
                MaxRows = max_rows,
                Cursor = string.IsNullOrWhiteSpace(cursor) ? next_cursor : cursor
            },
            ct.Token), cancellationToken);

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
