using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;
using Projector.Mcp.Server.Hosting;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Expense reports: list_expenses reads (any person the caller may see) and returns what a save needs;
/// save_expenses writes the caller's own draft cards and receipts. Nothing here submits or approves.
/// </summary>
[McpServerToolType]
public sealed class ExpenseTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ExpenseToolService _expenses;
    private readonly ConnectionResolver _connections;
    private readonly ILogger<ExpenseTools> _logger;

    public ExpenseTools(ExpenseToolService expenses, ConnectionResolver connections, ILogger<ExpenseTools> logger)
    {
        _expenses = expenses;
        _connections = connections;
        _logger = logger;
    }

    [McpServerTool(Name = "list_expenses", Title = "List Projector expense reports",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Lists expense reports for one person (default: the signed-in user; others only as Projector permits): " +
        "number, name, status (Draft, Submitted, Approved, Approved to Pay, Paid, partial states), dates, card count, " +
        "total, currency, projects. With report (an ER number) it returns that report's cost cards instead: date, " +
        "expense type, description, amount and currency, rate, amount in the report currency, location, project, " +
        "status, editable, receipt names, missing_receipt (Projector won't submit the card). include_options = true adds everything save_expenses needs: projects open " +
        "for expenses with their allowed expense types, expense types, locations, currencies, rules (receipt size), " +
        "closed days, receipts waiting in the user's pool, and receipt_upload: a URL and ticket to upload receipt files " +
        "from disk with curl (binary, as Projector itself does). Call it with include_options before save_expenses. " +
        ToolOutputSchemas.ExpensesSchemaHint + " " +
        "WhenNotToUse: Do not use for time cards; use list_timecards.")]
    public Task<CallToolResult> ListExpenses(
        [Description("Optional person: resource id, full name or e-mail. Omit (or \"me\") for the signed-in user.")] string? resource_id = null,
        [Description("Optional ER number, e.g. ER00123: show that report's cards instead of the list")] string? report = null,
        [Description("Months of reports to list, counted back from today (1-60, default 12)")] int months = ExpenseToolService.DefaultMonths,
        [Description("true: only reports not yet marked received")] bool unreceived_only = false,
        [Description("Optional words to match report names and projects, or card descriptions with report, or options projects")] string? query = null,
        [Description("true: add the projects, expense types, locations, currencies, rules, closed days and pool receipts needed to save")] bool include_options = false,
        [Description("Date (yyyy-MM-dd) the options are for; projects open in the 180 days before it are listed. Default today")] string? options_date = null,
        [Description("Optional project code: options for that project only")] string? project_code = null,
        [Description("Maximum option projects to return (1-200, default 50)")] int max_rows = ExpenseToolService.DefaultMaxProjects,
        [Description("Option projects to skip; use projects_next_offset from the previous page")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _expenses.ListExpensesAsync(
                ct.ConnectionId, resource_id, report, months, unreceived_only, query, include_options, options_date,
                project_code, max_rows, offset, ct.Token),
            cancellationToken);

    [McpServerTool(Name = "save_expenses", Title = "Save my Projector expenses (draft)",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Saves 1-20 cost cards with their receipts on one of the signed-in user's own expense reports: a new draft " +
        "report (report_name) or an existing editable one (report = ER number). New cards are drafts; card_uid changes " +
        "one of the user's draft or rejected cards. Never submits, approves or deletes; the user submits the report in " +
        "Projector. Get the values from list_expenses with include_options first. An amount in another currency is " +
        "converted with Projector's own rate for the card's date (shown as rate and amount_report_currency). A receipt " +
        "(PDF, PNG, JPEG or GIF, at most 2 MB) is one of: receipt_uid of a file in the user's pool (upload files from " +
        "disk first with list_expenses options.receipt_upload: curl, one file per request); source_url, a public https " +
        "link the server downloads; or content_base64 + file_name, only for files under about 10 KB, because a larger " +
        "file can't be written out in one call. " +
        "Every card is checked first; if one is invalid, nothing is saved. Call with dry_run = true, show the user every " +
        "card (date, project, type, description, amount, converted amount, receipt) and the total, and save only after " +
        "the user's explicit confirmation. After a save the report is read back: a card with status not_applied was " +
        "not saved as sent. Most expense types need a receipt before the report can be submitted: a card without one " +
        "gets the warning \"receipt required\", so ask the user for the receipt before saving. Relay every warning. " +
        ToolOutputSchemas.SaveExpensesSchemaHint + " " +
        "WhenNotToUse: Do not use to read reports; use list_expenses. Do not use for time; use save_timecard.")]
    public Task<CallToolResult> SaveExpenses(
        [Description("The cost cards to save (1-20), in the order the user approved them")] SaveExpenseCard[] cards,
        [Description("ER number of an existing editable report of yours to add to or change. Omit to create a new report")] string? report = null,
        [Description("Name of a new report, e.g. Trip to Toronto 4-11 Jul 2026 (required without report; with report it renames it)")] string? report_name = null,
        [Description("true: check every card and show the converted amounts, but save and upload nothing")] bool dry_run = false,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(ct => _expenses.SaveExpensesAsync(
                ct.ConnectionId,
                report,
                report_name,
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
