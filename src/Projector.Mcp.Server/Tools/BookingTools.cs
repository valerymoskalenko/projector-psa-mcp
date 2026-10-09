using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

/// <summary>save_booking: scheduler-mode hours on a project role. Never requests, submits or finalizes.</summary>
[McpServerToolType]
public sealed class BookingTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BookingToolService _bookings;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<BookingTools> _logger;

    public BookingTools(BookingToolService bookings, ConnectionResolver connections, ILogger<BookingTools> logger)
    {
        _bookings = bookings;
        _connections = connections;
        _logger = logger;
    }

    [McpServerTool(Name = "save_booking", Title = "Book a person on a Projector role",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Books one person on a project role for a date range (Resource Scheduling grid): hours land on the role, " +
        "not on the task plan. Weeks run Sunday–Saturday. With hours, each week in the range is replaced: hours is " +
        "the amount on each week (weekly; a week that runs past end_date is skipped) or on each working day Mon–Fri " +
        "(daily). Without hours, the current amounts stay and only the weeks of extra_days / comments are written. " +
        "extra_days add hours (and an optional comment) on dates inside start_date..end_date, on top of the week; " +
        "those weeks are saved as daily amounts (the week's hours spread Mon–Fri, then the extras added). To add " +
        "hours to an existing booking, omit hours. Optional comments are the cell tooltip notes. If the person has no role, one named after them is created; if a " +
        "task is given and the role is not on it, the role is assigned (effort stays 0). Never requests, submits or " +
        "finalizes. dry_run defaults to true: call, show the weeks (role name, previous and new hours, notes), and save " +
        "with dry_run = false only after the user's explicit confirmation. " +
        ToolOutputSchemas.SaveBookingSchemaHint + " " +
        "WhenNotToUse: Do not use to read bookings; use list_proj_bookings or get_schedule. Do not use for time " +
        "cards; use save_timecard.")]
    public Task<CallToolResult> SaveBooking(
        [Description("Person to book: resource id, full name or e-mail (required; never defaults to the signed-in user)")] string resource,
        [Description("Project code, e.g. C000123-001")] string project_code,
        [Description("Inclusive start date yyyy-MM-dd")] string start_date,
        [Description("Inclusive end date yyyy-MM-dd")] string end_date,
        [Description("Hours on each week (weekly) or each working day (daily); replaces those weeks. Omit to keep the current hours and only add extra_days / change comments")] double? hours = null,
        [Description("weekly = one total per Sunday–Saturday week; daily = hours on each Mon–Fri in the range")] string scheduling_mode = "weekly",
        [Description("Optional task WBS, path or name: used to pick the role and to assign the role to the task if missing")] string? task = null,
        [Description("Optional role name when the person has more than one role on the project")] string? role_name = null,
        [Description("Optional extra days: hours added on top of the week, with optional comment (tooltip text)")] BookingExtraDay[]? extra_days = null,
        [Description("Optional comments only (date + text). Omit a day to keep its note; empty text clears it")] BookingCommentDay[]? comments = null,
        [Description("Default true: check only, nothing is saved. Pass false to save, only after the user's explicit OK (true shows the before/after weeks)")] bool dry_run = true,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _bookings.SaveBookingAsync(
                ct.ConnectionId,
                resource,
                project_code,
                start_date,
                end_date,
                hours,
                scheduling_mode,
                task,
                role_name,
                (extra_days ?? []).Select(e => new BookingExtraDayInput(e.date, e.hours, e.comment)).ToList(),
                (comments ?? []).Select(c => new BookingCommentInput(c.date, c.text)).ToList(),
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

public sealed class BookingExtraDay
{
    public string date { get; set; } = "";
    public double hours { get; set; }
    public string? comment { get; set; }
}

public sealed class BookingCommentDay
{
    public string date { get; set; } = "";
    public string text { get; set; } = "";
}
