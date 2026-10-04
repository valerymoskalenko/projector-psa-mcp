using System.Globalization;
using Microsoft.Extensions.Logging;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Expenses;

namespace Projector.Application.Tools;

/// <summary>One cost card in a save_expenses call, as the MCP tool receives it.</summary>
public sealed record SaveExpenseInput(
    string Date,
    string ProjectCode,
    string ExpenseType,
    string? Description,
    double Amount,
    string? Currency = null,
    string? Location = null,
    string? CardUid = null,
    string? ReceiptFileName = null,
    string? ReceiptContentBase64 = null,
    string? ReceiptUid = null);

/// <summary>
/// Expense reports: list_expenses reads reports, one report's cards and everything needed to save (for any person
/// the caller may see); save_expenses writes the caller's own draft cards and receipts, one report per call.
/// A save never submits: the user submits in Projector. Every card is checked before anything is written; one
/// invalid card refuses the whole call, because Projector saves a report atomically.
/// </summary>
public sealed class ExpenseToolService
{
    public const int MaxCardsPerSave = 20;
    public const int MaxDescriptionLength = 255;
    /// <summary>Projector's DocumentName limit for a receipt.</summary>
    public const int MaxReceiptNameLength = 100;
    /// <summary>Used when Projector reports no receipt quota.</summary>
    public const long DefaultReceiptMaxBytes = 2 * 1024 * 1024;
    public const long MaxReceiptBytesPerCall = 8 * 1024 * 1024;
    public const int DefaultMonths = 12;
    public const int DefaultMaxProjects = 50;
    public const int MaxProjectsLimit = 200;
    /// <summary>Days before options_date whose projects are offered (receipts are often entered weeks later).</summary>
    private const int OptionsLookbackDays = 180;

    public static readonly IReadOnlySet<string> ReceiptExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".gif" };

    private const string SelfKind = "expense_self";
    private const string RulesKind = "expense_rules";
    private const string EntryKind = "expense_entry";
    private const string CurrencyKind = "expense_currency";
    private const string RatesKind = "expense_rates";
    private const string PoolKind = "expense_pool";
    private const string ReceiptRulesKind = "expense_receipt_rules";

    /// <summary>Warning on a card whose expense type needs a receipt before the report can be submitted.</summary>
    public const string ReceiptRequiredWarning =
        "receipt required: Projector won't submit this card without a receipt; ask the user for it (or add it in Projector later)";
    private const string WriteOutcomeUnknown = "write_outcome_unknown";

    private static readonly IReadOnlyDictionary<string, string> ReportStatusNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = "Draft",
        ["PR"] = "Partially Rejected",
        ["R"] = "Rejected",
        ["PS"] = "Partially Submitted",
        ["S"] = "Submitted",
        ["PA"] = "Partially Approved",
        ["A"] = "Approved",
        ["F"] = "Approved to Pay",
        ["P"] = "Paid"
    };

    private static readonly IReadOnlyDictionary<string, string> CardStatusNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = "Draft",
        ["R"] = "Rejected",
        ["S"] = "Submitted",
        ["A"] = "Approved"
    };

    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorExpenseClient _expenses;
    private readonly TimeEntryCache _cache;
    private readonly ProjectorToolService _tools;
    private readonly ILogger<ExpenseToolService> _logger;

    public ExpenseToolService(
        ProjectorConnectionService connections,
        IProjectorExpenseClient expenses,
        TimeEntryCache cache,
        ProjectorToolService tools,
        ILogger<ExpenseToolService> logger)
    {
        _connections = connections;
        _expenses = expenses;
        _cache = cache;
        _tools = tools;
        _logger = logger;
    }

    // ---------------------------------------------------------------- list_expenses

    public async Task<object> ListExpensesAsync(
        string connectionId,
        string? resourceId,
        string? report,
        int months,
        bool unreceivedOnly,
        string? query,
        bool includeOptions,
        string? optionsDate,
        string? projectCode,
        int maxRows,
        int offset,
        CancellationToken ct)
    {
        months = Math.Clamp(months <= 0 ? DefaultMonths : months, 1, 60);
        maxRows = Math.Clamp(maxRows <= 0 ? DefaultMaxProjects : maxRows, 1, MaxProjectsLimit);
        offset = Math.Max(0, offset);
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var date = string.IsNullOrWhiteSpace(optionsDate) ? Today() : ParseDate(optionsDate, "options_date");
        var connection = await RequireAsync(connectionId, ct);

        var (personId, label) = await _tools.ResolveResourceArgAsync(connection, resourceId, ct);
        var isSelf = personId is null;

        object? reportView = null;
        object? reportsView = null;
        if (!string.IsNullOrWhiteSpace(report))
        {
            var number = report.Trim();
            var detail = await WithRefreshAsync(connection, c => _expenses.GetReportAsync(c, number, ct), ct)
                ?? throw new ProjectorApiException(
                    $"Expense report '{number}' was not found, or you may not see it. list_expenses without report lists the reports.",
                    "ExpenseDocumentNotFound");
            var rules = detail.ResourceId is null || detail.Currency is null
                ? null
                : await TryGetReceiptRulesAsync(connection, detail.ResourceId, detail.Currency, ct);
            reportView = DescribeReport(detail, text, rules);
        }
        else
        {
            var list = await WithRefreshAsync(connection, c => _expenses.ListReportsAsync(c, personId, months, unreceivedOnly, ct), ct);
            var rows = list.Reports
                .Where(r => text is null || TextMatch.Matches(text, r.Number, r.Name,
                    string.Join(" ", r.Projects.Select(p => p.Code + " " + p.Name))))
                .OrderByDescending(r => r.LatestDate ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(r => r.Number ?? string.Empty, StringComparer.Ordinal)
                .Select(DescribeSummary)
                .ToList();
            reportsView = new { count = rows.Count, months, rows, can_create = isSelf ? list.CanCreate : (bool?)null };
        }

        object? options = null;
        if (includeOptions)
        {
            options = await DescribeOptionsAsync(connection, personId, isSelf, date, projectCode, text, maxRows, offset, ct);
        }

        return new
        {
            resource_id = label,
            reports = reportsView,
            report = reportView,
            options,
            note = includeOptions
                ? "save_expenses saves the signed-in user's own draft cards only; it never submits."
                : "Call again with include_options = true before save_expenses: it lists the projects, expense types, locations, currencies and open days."
        };
    }

    private async Task<object> DescribeOptionsAsync(
        ProjectorConnection connection,
        string? personId,
        bool isSelf,
        string date,
        string? projectCode,
        string? query,
        int maxRows,
        int offset,
        CancellationToken ct)
    {
        string resource;
        ExpenseIdentity? self = null;
        if (isSelf)
        {
            self = await RequireSelfAsync(connection, ct);
            resource = self.ResourceId;
        }
        else
        {
            resource = personId!;
        }

        var start = AddDays(date, -OptionsLookbackDays);
        var info = await GetEntryInfoAsync(connection, resource, start, date, ct);
        var rules = await GetRulesAsync(connection, ct);
        var currency = await GetReportCurrencyAsync(connection, resource, ct);
        var rates = currency is null ? [] : await GetRatesAsync(connection, resource, currency, date, ct);
        var receiptRules = currency is null ? null : await TryGetReceiptRulesAsync(connection, resource, currency, ct);
        var days = await WithRefreshAsync(connection,
            c => _expenses.GetScheduleAsync(c, isSelf ? null : resource, AddDays(date, -30), AddDays(date, 7), ct), ct);

        var code = string.IsNullOrWhiteSpace(projectCode) ? null : projectCode.Trim();
        var projects = info.Projects
            .Where(p => code is null || string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
            .Where(p => code is not null || query is null
                || TextMatch.Matches(query, p.Code, p.Name, p.ClientName, p.EngagementName))
            .OrderBy(p => p.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = projects.Skip(offset).Take(maxRows).ToList();

        object? pool = null;
        if (self?.UserUid is not null)
        {
            try
            {
                var receiptPool = await GetPoolAsync(connection, self.UserUid, ct);
                var items = await WithRefreshAsync(connection, c => _expenses.ListPoolAsync(c, receiptPool.FolderUid, ct), ct);
                pool = items.Select(r => new { receipt_uid = r.DocumentUid, name = r.Name, size_kb = Kb(r.Size), uploaded = r.Created }).ToList();
            }
            catch (ProjectorApiException ex)
            {
                _logger.LogWarning(ex, "Receipt pool could not be read ({ErrorCode})", ex.ErrorCode);
            }
        }

        return new
        {
            options_date = date,
            projects = page.Select(p => new
            {
                project_code = p.Code,
                name = p.Name,
                client = p.ClientName,
                engagement = p.EngagementName,
                open = p.OpenDate,
                close = p.CloseDate,
                expense_types = p.AnyExpenseType ? (object)"any" : p.ExpenseTypes
            }).ToList(),
            projects_total = projects.Count,
            projects_has_more = offset + page.Count < projects.Count,
            projects_next_offset = offset + page.Count < projects.Count ? offset + page.Count : (int?)null,
            expense_types = info.ExpenseTypes.Select(t => new
            {
                name = t.Name,
                group = t.Group,
                description_required = t.DescriptionRequired,
                instructions = t.Instructions,
                receipt_required = ReceiptRequirement(receiptRules, t.Name, currency),
                supported = !(t.Mileage || t.UnitDriven),
                note = t.Mileage ? "mileage: enter in Projector" : t.UnitDriven ? "per unit: enter in Projector" : null
            }).ToList(),
            locations = info.Locations,
            report_currency = currency,
            currencies = rates.Select(r => r.Code).ToList(),
            rules = new
            {
                receipts_on_cards = rules.ReceiptsOnCards,
                receipts_on_report = rules.ReceiptsOnReport,
                receipt_max_kb = Kb(ReceiptMaxBytes(rules)),
                receipt_types = ReceiptExtensions.Order(StringComparer.Ordinal).ToList(),
                non_billable_allowed = rules.NonBillableAllowed,
                outside_project_dates_allowed = rules.OutsideProjectDatesAllowed,
                location_required = rules.LocationRequired,
                entry_for_others_allowed = rules.EntryForOthersAllowed
            },
            closed_days = days.Where(d => !d.CanEnter || d.PeriodClosed).Select(d => d.Date).ToList(),
            days_checked = days.Count == 0 ? null : $"{days.First().Date}..{days.Last().Date}",
            receipt_pool = pool
        };
    }

    // ---------------------------------------------------------------- save_expenses

    private sealed record CardPlan(
        int Index,
        SaveExpenseInput Input,
        List<string> Errors,
        List<string> Warnings)
    {
        public string ReferenceId => "card" + Index.ToString(CultureInfo.InvariantCulture);
        public string ReceiptReferenceId => "receipt" + Index.ToString(CultureInfo.InvariantCulture);
        public string? Date { get; set; }
        public string? ProjectCode { get; set; }
        public string? ExpenseType { get; set; }
        public string? Description { get; set; }
        public string? Currency { get; set; }
        public double? Rate { get; set; }
        public double? Disbursed { get; set; }
        public string? Location { get; set; }
        public ExpenseCard? Existing { get; set; }
        public byte[]? ReceiptBytes { get; set; }
        public string? ReceiptName { get; set; }
        public PoolReceipt? PoolReceipt { get; set; }
        public UploadedReceipt? Uploaded { get; set; }
        public ExpenseCard? Saved { get; set; }
        public bool ReceiptLinked { get; set; }
        public string Status { get; set; } = "valid";
        public bool HasReceipt => ReceiptBytes is not null || PoolReceipt is not null;
        public string? ReceiptDocumentUid => Uploaded?.DocumentUid ?? PoolReceipt?.DocumentUid;
    }

    public async Task<object> SaveExpensesAsync(
        string connectionId,
        string? report,
        string? reportName,
        IReadOnlyList<SaveExpenseInput>? cards,
        bool dryRun,
        CancellationToken ct)
    {
        if (cards is null || cards.Count == 0)
        {
            throw new ArgumentException("cards is required: 1 to 20 cost cards.");
        }

        if (cards.Count > MaxCardsPerSave)
        {
            throw new ArgumentException($"At most {MaxCardsPerSave} cards per call; split the report into several calls.");
        }

        var connection = await RequireAsync(connectionId, ct);
        var self = await RequireSelfAsync(connection, ct);

        // The report: an existing editable report of the caller, or a new one.
        ExpenseReportDetail? existing = null;
        string name;
        if (!string.IsNullOrWhiteSpace(report))
        {
            var number = report.Trim();
            existing = await WithRefreshAsync(connection, c => _expenses.GetReportAsync(c, number, ct), ct)
                ?? throw new ProjectorApiException($"Expense report '{number}' was not found.", "ExpenseDocumentNotFound");
            if (!IsOwnReport(existing, self))
            {
                throw new ProjectorApiException(
                    $"Expense report {existing.Number} belongs to {existing.ResourceName ?? "another person"}; save_expenses writes only your own reports.",
                    "NotOwnExpenseReport");
            }

            if (!existing.Editable)
            {
                throw new ProjectorApiException(
                    $"Expense report {existing.Number} can't be changed ({DescribeLock(existing)}). Start a new report with report_name.",
                    "ExpenseReportLocked");
            }

            name = string.IsNullOrWhiteSpace(reportName) ? existing.Name ?? existing.Number! : reportName.Trim();
        }
        else
        {
            name = string.IsNullOrWhiteSpace(reportName)
                ? throw new ArgumentException("Give report_name for a new expense report, or report (an ER number) to add to a draft report.")
                : reportName.Trim();
        }

        var currency = existing?.Currency ?? await GetReportCurrencyAsync(connection, self.ResourceId, ct)
            ?? throw new ProjectorApiException("Projector did not say which currency your expense reports are paid in.", "NoReportCurrency");
        var digits = existing?.CurrencyDigits ?? 2;

        var plans = cards.Select((c, i) => new CardPlan(i, c, [], [])).ToList();
        await ValidateAsync(connection, self, existing, currency, digits, plans, ct);

        var invalid = plans.Count(p => p.Errors.Count > 0);
        if (invalid > 0 || dryRun)
        {
            foreach (var plan in plans)
            {
                plan.Status = plan.Errors.Count > 0 ? "invalid" : "valid";
            }

            LogAudit(dryRun ? "dry_run" : "refused", existing, plans, currency);
            return Result(
                dryRun ? "dry_run" : "refused", existing?.Number, name, currency, plans, existing,
                invalid > 0
                    ? $"Nothing was saved: {invalid} card(s) are invalid. Fix them and send all cards again (Projector saves a report as a whole)."
                    : "Dry run: nothing was saved or uploaded. Show the user the cards and amounts, then call again without dry_run after an explicit \"save\".",
                []);
        }

        // Receipts first: an upload failure stops the call before anything is written to the report.
        var leftInPool = new List<object>();
        var uploads = plans.Where(p => p.ReceiptBytes is not null).ToList();
        if (uploads.Count > 0)
        {
            if (self.UserUid is null)
            {
                throw new ProjectorApiException("Projector did not return your user, so receipts can't be uploaded.", "NoReceiptFolder");
            }

            var pool = await GetPoolAsync(connection, self.UserUid, ct);
            foreach (var plan in uploads)
            {
                try
                {
                    plan.Uploaded = await _expenses.UploadReceiptAsync(connection, pool, plan.ReceiptName!, plan.ReceiptBytes!, ct);
                    leftInPool.Add(new { receipt_uid = plan.Uploaded.DocumentUid, name = plan.Uploaded.Name, card = plan.Index });
                }
                catch (ProjectorApiException ex)
                {
                    plan.Status = "failed";
                    plan.Errors.Add(ex.Message);
                    foreach (var other in plans.Where(p => p.Status == "valid"))
                    {
                        other.Status = "not_attempted";
                    }

                    _cache.Remove(connection, PoolKind, self.UserUid);
                    LogAudit("upload_failed", existing, plans, currency);
                    return Result("failed", existing?.Number, name, currency, plans, existing,
                        "Nothing was saved to the report: a receipt upload failed. Receipts uploaded before it wait in your receipt pool.",
                        leftInPool, ex.ErrorCode == WriteOutcomeUnknown ? WriteOutcomeUnknown : null);
                }
            }
        }

        var request = new ExpenseSaveRequest
        {
            ReportUid = existing?.Uid,
            ReportTimestamp = existing?.Timestamp,
            ReportName = name,
            ResourceId = self.ResourceId,
            Cards = plans.Select(p => new ExpenseCardWrite
            {
                ReferenceId = p.ReferenceId,
                CardUid = p.Existing?.Uid,
                Timestamp = p.Existing?.Timestamp,
                Date = p.Date!,
                ExpenseType = p.ExpenseType!,
                Description = p.Description,
                Amount = p.Input.Amount,
                Currency = p.Currency!,
                DisbursedAmount = p.Disbursed!.Value,
                ProjectCode = p.ProjectCode!,
                Location = p.Location
            }).ToList(),
            Receipts = plans.Where(p => p.ReceiptDocumentUid is not null).Select(p => new ExpenseReceiptLink
            {
                DocumentUid = p.ReceiptDocumentUid!,
                DocumentName = Truncate(p.Uploaded?.Name ?? p.PoolReceipt?.Name ?? p.ReceiptName ?? "receipt", MaxReceiptNameLength),
                ReferenceId = p.ReceiptReferenceId,
                CardUid = p.Existing?.Uid,
                CardReferenceId = p.Existing is null ? p.ReferenceId : null
            }).ToList()
        };

        ExpenseSaveResult saved;
        try
        {
            saved = await WithRefreshAsync(connection, c => _expenses.SaveAsync(c, request, ct), ct);
        }
        catch (ProjectorApiException ex)
        {
            throw MapSaveError(ex);
        }
        finally
        {
            if (self.UserUid is not null)
            {
                _cache.Remove(connection, PoolKind, self.UserUid);
            }
        }

        if (!saved.Succeeded)
        {
            ApplyIssues(plans, saved);
            LogAudit("save_failed", existing, plans, currency);
            var loose = saved.CardIssues.Concat(saved.ReceiptIssues)
                .Where(i => !plans.Any(p => i.ReferenceId == p.ReferenceId || i.ReferenceId == p.ReceiptReferenceId))
                .Select(i => i.Text ?? i.Code)
                .OfType<string>();
            return Result("failed", existing?.Number, name, currency, plans, existing,
                "Projector refused the save; nothing was changed. "
                + string.Join(" ", saved.Messages.Select(m => m.Text).OfType<string>().Concat(loose)),
                leftInPool);
        }

        var after = saved.Report
            ?? (saved.Number is null ? null : await WithRefreshAsync(connection, c => _expenses.GetReportAsync(c, saved.Number, ct), ct));
        Match(plans, existing, after);

        // A receipt linked to a new card by its ReferenceId: if Projector did not link it, link it by the card's UID.
        var unlinked = plans.Where(p => p.ReceiptDocumentUid is not null && p.Saved?.Uid is not null && !p.ReceiptLinked).ToList();
        if (after?.Uid is not null && after.Timestamp is not null && unlinked.Count > 0)
        {
            var relink = new ExpenseSaveRequest
            {
                ReportUid = after.Uid,
                ReportTimestamp = after.Timestamp,
                ReportName = name,
                ResourceId = self.ResourceId,
                Receipts = unlinked.Select(p => new ExpenseReceiptLink
                {
                    DocumentUid = p.ReceiptDocumentUid!,
                    DocumentName = Truncate(p.Uploaded?.Name ?? p.PoolReceipt?.Name ?? p.ReceiptName ?? "receipt", MaxReceiptNameLength),
                    CardUid = p.Saved!.Uid
                }).ToList()
            };
            try
            {
                var second = await WithRefreshAsync(connection, c => _expenses.SaveAsync(c, relink, ct), ct);
                after = second.Report ?? after;
                Match(plans, existing, after);
            }
            catch (ProjectorApiException ex)
            {
                _logger.LogWarning(ex, "save_expenses receipt link by card UID failed ({ErrorCode})", ex.ErrorCode);
            }
        }

        var linkedUids = plans.Where(p => p.ReceiptLinked).Select(p => p.ReceiptDocumentUid).ToHashSet(StringComparer.Ordinal);
        var stillInPool = plans
            .Where(p => p.Uploaded is not null && !linkedUids.Contains(p.Uploaded.DocumentUid))
            .Select(p => (object)new { receipt_uid = p.Uploaded!.DocumentUid, name = p.Uploaded.Name, card = p.Index })
            .ToList();

        var notApplied = plans.Count(p => p.Status == "not_applied");
        LogAudit("save", existing, plans, currency);
        return Result("saved", after?.Number ?? saved.Number, after?.Name ?? name, currency, plans, after,
            notApplied == 0 && stillInPool.Count == 0
                ? "Saved as draft cards, not submitted. The user submits the report in Projector when it is complete."
                : "Saved, but Projector did not apply everything: see the cards with status not_applied and the receipts left in the pool. Check the report in Projector.",
            stillInPool);
    }

    private async Task ValidateAsync(
        ProjectorConnection connection,
        ExpenseIdentity self,
        ExpenseReportDetail? existing,
        string currency,
        int digits,
        List<CardPlan> plans,
        CancellationToken ct)
    {
        var rules = await GetRulesAsync(connection, ct);
        foreach (var plan in plans)
        {
            plan.Date = TryParseDate(plan.Input.Date);
            if (plan.Date is null)
            {
                plan.Errors.Add("date must be yyyy-MM-dd.");
            }
        }

        var dates = plans.Select(p => p.Date).OfType<string>().Order(StringComparer.Ordinal).ToList();
        if (dates.Count == 0)
        {
            return;
        }

        var info = await GetEntryInfoAsync(connection, self.ResourceId, dates[0], dates[^1], ct);
        var days = (await WithRefreshAsync(connection, c => _expenses.GetScheduleAsync(c, null, dates[0], dates[^1], ct), ct))
            .ToDictionary(d => d.Date, StringComparer.Ordinal);
        var maxBytes = ReceiptMaxBytes(rules);
        var receiptRules = await TryGetReceiptRulesAsync(connection, self.ResourceId, currency, ct);
        IReadOnlyList<PoolReceipt>? pool = null;
        long totalBytes = 0;

        foreach (var plan in plans)
        {
            var input = plan.Input;
            var errors = plan.Errors;

            // Card being updated.
            if (!string.IsNullOrWhiteSpace(input.CardUid))
            {
                if (existing is null)
                {
                    errors.Add("card_uid needs report (the ER number of the card's report).");
                }
                else
                {
                    plan.Existing = existing.Cards.FirstOrDefault(c => string.Equals(c.Uid, input.CardUid.Trim(), StringComparison.Ordinal));
                    if (plan.Existing is null)
                    {
                        errors.Add($"card_uid {input.CardUid} is not on report {existing.Number}.");
                    }
                    else if (!plan.Existing.Editable)
                    {
                        errors.Add($"Card {input.CardUid} is {CardStatusName(plan.Existing.ApprovalStatus)} and can't be changed.");
                    }
                }
            }

            // Project and expense type.
            plan.ProjectCode = input.ProjectCode?.Trim();
            var project = info.Projects.FirstOrDefault(p => string.Equals(p.Code, plan.ProjectCode, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(plan.ProjectCode))
            {
                errors.Add("project_code is required.");
            }
            else if (project is null)
            {
                errors.Add($"Project {plan.ProjectCode} is not open for your expenses on these dates (see list_expenses include_options).");
            }
            else
            {
                plan.ProjectCode = project.Code;
                if (plan.Date is not null && !rules.OutsideProjectDatesAllowed
                    && ((project.OpenDate is not null && string.CompareOrdinal(plan.Date, project.OpenDate) < 0)
                        || (project.CloseDate is not null && string.CompareOrdinal(plan.Date, project.CloseDate) > 0)))
                {
                    errors.Add($"{plan.Date} is outside the dates of project {project.Code} ({project.OpenDate}..{project.CloseDate ?? "open"}).");
                }
            }

            var type = info.ExpenseTypes.FirstOrDefault(t => string.Equals(t.Name, input.ExpenseType?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (type?.Name is null)
            {
                errors.Add($"Unknown expense type '{input.ExpenseType}'. Use a name from list_expenses options.expense_types.");
            }
            else
            {
                plan.ExpenseType = type.Name;
                if (type.Mileage || type.UnitDriven)
                {
                    errors.Add($"'{type.Name}' is entered per unit or per mile; enter it in Projector.");
                }

                if (project is not null && !project.AnyExpenseType
                    && !project.ExpenseTypes.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"'{type.Name}' is not allowed on {project.Code}; allowed: {string.Join(", ", project.ExpenseTypes)}.");
                }

                plan.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
                if (plan.Description is null && type.DescriptionRequired)
                {
                    errors.Add($"'{type.Name}' needs a description.");
                }
            }

            if (plan.Description is { Length: > MaxDescriptionLength })
            {
                errors.Add($"description is longer than {MaxDescriptionLength} characters.");
            }

            // Amount and day.
            if (!(input.Amount > 0) || input.Amount > 1_000_000 || double.IsNaN(input.Amount))
            {
                errors.Add("amount must be more than 0 (the amount on the receipt, in its currency).");
            }

            if (plan.Date is not null && days.TryGetValue(plan.Date, out var day) && (!day.CanEnter || day.PeriodClosed))
            {
                errors.Add($"{plan.Date} is closed for expenses in Projector{(day.PeriodClosed ? " (accounting period closed)" : string.Empty)}.");
            }

            // Location.
            if (!string.IsNullOrWhiteSpace(input.Location))
            {
                plan.Location = info.Locations.FirstOrDefault(l => string.Equals(l, input.Location.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? info.Locations.FirstOrDefault(l => l.StartsWith(input.Location.Trim() + " ", StringComparison.OrdinalIgnoreCase));
                if (plan.Location is null)
                {
                    errors.Add($"Unknown location '{input.Location}'; use one of: {string.Join(", ", info.Locations)}.");
                }
            }
            else if (rules.LocationRequired)
            {
                errors.Add("Projector requires a location on every card.");
            }

            // Currency and Projector's rate on the card's date.
            plan.Currency = string.IsNullOrWhiteSpace(input.Currency) ? currency : input.Currency.Trim().ToUpperInvariant();
            if (plan.Date is not null && input.Amount > 0)
            {
                if (string.Equals(plan.Currency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Rate = 1;
                    plan.Disbursed = Math.Round(input.Amount, digits, MidpointRounding.AwayFromZero);
                }
                else
                {
                    var rates = await GetRatesAsync(connection, self.ResourceId, currency, plan.Date, ct);
                    var rate = rates.FirstOrDefault(r => string.Equals(r.Code, plan.Currency, StringComparison.OrdinalIgnoreCase));
                    if (rate is null)
                    {
                        errors.Add($"Unknown currency '{plan.Currency}'.");
                    }
                    else if (rate.Rate is not > 0)
                    {
                        errors.Add($"Projector has no {plan.Currency}→{currency} rate for {plan.Date}.");
                    }
                    else
                    {
                        plan.Rate = rate.Rate;
                        plan.Disbursed = ConvertAmount(input.Amount, rate.Rate.Value, digits);
                    }
                }
            }

            // Receipt: a file (base64) or a receipt already in the pool.
            var hasFile = !string.IsNullOrWhiteSpace(input.ReceiptContentBase64);
            var hasPool = !string.IsNullOrWhiteSpace(input.ReceiptUid);
            if (hasFile && hasPool)
            {
                errors.Add("Give either receipt.content_base64 or receipt.receipt_uid, not both.");
            }
            else if (hasFile || hasPool)
            {
                if (!rules.ReceiptsOnCards)
                {
                    errors.Add("Projector does not allow receipts on cost cards in this account.");
                }
                else if (hasFile)
                {
                    var fileName = Path.GetFileName(input.ReceiptFileName?.Trim() ?? string.Empty);
                    if (string.IsNullOrEmpty(fileName) || !ReceiptExtensions.Contains(Path.GetExtension(fileName)))
                    {
                        errors.Add($"receipt.file_name must end in {string.Join(", ", ReceiptExtensions.Order(StringComparer.Ordinal))}.");
                    }
                    else
                    {
                        plan.ReceiptName = Truncate(fileName, MaxReceiptNameLength);
                        plan.ReceiptBytes = TryDecode(input.ReceiptContentBase64!);
                        if (plan.ReceiptBytes is null)
                        {
                            errors.Add("receipt.content_base64 is not valid base64.");
                        }
                        else if (plan.ReceiptBytes.Length == 0)
                        {
                            errors.Add("The receipt file is empty.");
                        }
                        else if (plan.ReceiptBytes.Length > maxBytes)
                        {
                            errors.Add($"The receipt is {Mb(plan.ReceiptBytes.Length)} MB; Projector accepts up to {Mb(maxBytes)} MB. Compress or scale it down.");
                        }
                        else
                        {
                            totalBytes += plan.ReceiptBytes.Length;
                        }
                    }
                }
                else
                {
                    if (self.UserUid is null)
                    {
                        errors.Add("Your receipt pool could not be found.");
                    }
                    else
                    {
                        pool ??= await WithRefreshAsync(connection, async c =>
                            await _expenses.ListPoolAsync(c, (await GetPoolAsync(c, self.UserUid, ct)).FolderUid, ct), ct);
                        plan.PoolReceipt = pool.FirstOrDefault(r => string.Equals(r.DocumentUid, input.ReceiptUid!.Trim(), StringComparison.Ordinal));
                        if (plan.PoolReceipt is null)
                        {
                            errors.Add($"Receipt {input.ReceiptUid} is not in your receipt pool (list_expenses include_options lists it).");
                        }
                    }
                }
            }
            else
            {
                var rule = receiptRules?.FirstOrDefault(r => string.Equals(r.Name, plan.ExpenseType, StringComparison.OrdinalIgnoreCase));
                plan.Warnings.Add(rule is null ? "no receipt" : rule.AppliesTo(plan.Disbursed) ? ReceiptRequiredWarning : "no receipt (not required for this type)");
            }
        }

        if (totalBytes > MaxReceiptBytesPerCall)
        {
            plans.First(p => p.ReceiptBytes is not null).Errors.Add(
                $"The receipts add up to {Mb(totalBytes)} MB; send at most {Mb(MaxReceiptBytesPerCall)} MB per call (split the cards).");
        }

        var poolUids = plans.Where(p => p.PoolReceipt is not null).GroupBy(p => p.PoolReceipt!.DocumentUid).Where(g => g.Count() > 1);
        foreach (var group in poolUids)
        {
            foreach (var plan in group.Skip(1))
            {
                plan.Errors.Add($"Receipt {group.Key} is used by more than one card in this call.");
            }
        }

        // Likely duplicates: same date, type and amount on the report or earlier in this call.
        foreach (var plan in plans.Where(p => p.Existing is null && p.Errors.Count == 0))
        {
            var onReport = existing?.Cards.FirstOrDefault(c => SameCard(c, plan));
            if (onReport is not null)
            {
                plan.Warnings.Add($"likely duplicate of card {onReport.Uid} on the report");
            }

            var earlier = plans.FirstOrDefault(p => p.Index < plan.Index && p.Errors.Count == 0
                && p.Date == plan.Date && p.ExpenseType == plan.ExpenseType && Math.Abs(p.Input.Amount - plan.Input.Amount) < 0.005
                && string.Equals(p.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase));
            if (earlier is not null)
            {
                plan.Warnings.Add($"same date, type and amount as card {earlier.Index} in this call");
            }
        }
    }

    internal static bool IsOwnReport(ExpenseReportDetail report, ExpenseIdentity self) =>
        report.ResourceUid is not null && self.ResourceUid is not null
            ? string.Equals(report.ResourceUid, self.ResourceUid, StringComparison.Ordinal)
            : string.Equals(report.ResourceId, self.ResourceId, StringComparison.OrdinalIgnoreCase);

    private static bool SameCard(ExpenseCard card, CardPlan plan) =>
        card.Date == plan.Date
        && string.Equals(card.ExpenseType, plan.ExpenseType, StringComparison.OrdinalIgnoreCase)
        && card.Amount is { } amount && Math.Abs(amount - plan.Input.Amount) < 0.005
        && string.Equals(card.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase);

    /// <summary>Projector's rate converts the receipt's amount into the report currency, rounded to its digits.</summary>
    internal static double ConvertAmount(double amount, double rate, int digits) =>
        Math.Round(amount * rate, digits, MidpointRounding.AwayFromZero);

    /// <summary>Finds each card in the report after the save; whatever is missing is not_applied.</summary>
    private static void Match(List<CardPlan> plans, ExpenseReportDetail? before, ExpenseReportDetail? after)
    {
        var beforeUids = before?.Cards.Select(c => c.Uid).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var newCards = after?.Cards.Where(c => c.Uid is not null && !beforeUids.Contains(c.Uid)).ToList() ?? [];
        foreach (var plan in plans)
        {
            ExpenseCard? found;
            if (plan.Existing is not null)
            {
                found = after?.Cards.FirstOrDefault(c => c.Uid == plan.Existing.Uid);
                if (found is not null && !(SameCard(found, plan) && found.ProjectCode == plan.ProjectCode
                    && string.Equals(found.Description, plan.Description, StringComparison.Ordinal)))
                {
                    found = null;
                }
            }
            else
            {
                found = newCards.FirstOrDefault(c => SameCard(c, plan)
                    && string.Equals(c.ProjectCode, plan.ProjectCode, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    newCards.Remove(found);
                }
            }

            plan.Saved = found;
            plan.ReceiptLinked = plan.ReceiptDocumentUid is not null && found?.Uid is not null
                && (after?.Receipts.Any(r => r.DocumentUid == plan.ReceiptDocumentUid && r.CardUids.Contains(found.Uid)) ?? false);
            plan.Status = found is null || (plan.ReceiptDocumentUid is not null && !plan.ReceiptLinked) ? "not_applied" : "saved";
        }
    }

    private static void ApplyIssues(List<CardPlan> plans, ExpenseSaveResult saved)
    {
        foreach (var plan in plans)
        {
            var issues = saved.CardIssues.Where(i => i.ReferenceId == plan.ReferenceId)
                .Concat(saved.ReceiptIssues.Where(i => i.ReferenceId == plan.ReceiptReferenceId)
                    .Select(i => i with { Text = "receipt: " + (i.Text ?? i.Code) }))
                .ToList();
            plan.Status = issues.Count > 0 ? "failed" : "not_attempted";
            plan.Errors.AddRange(issues.Select(i => i.Text ?? i.Code ?? "rejected by Projector"));
        }
    }

    private object Result(
        string action,
        string? number,
        string? name,
        string currency,
        List<CardPlan> plans,
        ExpenseReportDetail? report,
        string note,
        IReadOnlyList<object> receiptsInPool,
        string? error = null)
    {
        int Count(string status) => plans.Count(p => p.Status == status);
        var batchTotal = plans.Where(p => p.Disbursed is not null).Sum(p => p.Disbursed!.Value);
        return new
        {
            action,
            error,
            report = new
            {
                number,
                name,
                currency,
                total = report?.Total,
                card_count = report?.Cards.Count,
                status = ReportStatusName(report?.Status)
            },
            results = plans.Select(p => new
            {
                index = p.Index,
                status = p.Status,
                action = p.Existing is null ? "create" : "update",
                card = new
                {
                    card_uid = p.Saved?.Uid ?? p.Existing?.Uid,
                    date = p.Date ?? p.Input.Date,
                    project_code = p.ProjectCode ?? p.Input.ProjectCode,
                    expense_type = p.ExpenseType ?? p.Input.ExpenseType,
                    description = p.Description ?? p.Input.Description,
                    amount = p.Input.Amount,
                    currency = p.Currency,
                    rate = p.Rate is null or 1.0 ? (double?)null : Math.Round(p.Rate.Value, 8),
                    amount_report_currency = p.Disbursed,
                    location = p.Location
                },
                receipt = p.HasReceipt
                    ? new
                    {
                        name = p.Uploaded?.Name ?? p.PoolReceipt?.Name ?? p.ReceiptName,
                        size_kb = Kb(p.ReceiptBytes?.Length ?? p.PoolReceipt?.Size),
                        receipt_uid = p.ReceiptDocumentUid,
                        linked = action == "saved" ? p.ReceiptLinked : (bool?)null
                    }
                    : null,
                errors = p.Errors.Count == 0 ? null : p.Errors,
                warnings = p.Warnings.Count == 0 ? null : p.Warnings
            }).ToList(),
            cards_total = Math.Round(batchTotal, 2),
            saved_count = Count("saved"),
            valid_count = action is "dry_run" or "refused" ? Count("valid") : (int?)null,
            invalid_count = Count("invalid"),
            failed_count = Count("failed"),
            not_applied_count = Count("not_applied"),
            receipts_in_pool = receiptsInPool.Count == 0 ? null : receiptsInPool,
            submitted = false,
            note
        };
    }

    /// <summary>One line per save_expenses call: counts, codes, projects, currencies and totals; no descriptions or files.</summary>
    private void LogAudit(string action, ExpenseReportDetail? existing, List<CardPlan> plans, string currency)
    {
        int Count(string status) => plans.Count(p => p.Status == status);
        _logger.LogInformation(
            "save_expenses audit: {SaveAction} {CardCount} card(s) on {Report}: {Saved} saved, {Valid} valid, {Invalid} invalid, " +
            "{Failed} failed, {NotApplied} not applied; receipts {ReceiptCount} ({ReceiptKb} KB); projects [{ProjectCodes}]; " +
            "currencies [{Currencies}] -> {ReportCurrency}; total {Total}",
            action,
            plans.Count,
            existing?.Number ?? "new report",
            Count("saved"),
            Count("valid"),
            Count("invalid"),
            Count("failed"),
            Count("not_applied"),
            plans.Count(p => p.HasReceipt),
            Kb(plans.Sum(p => (long)(p.ReceiptBytes?.Length ?? 0))),
            string.Join(",", plans.Select(p => p.ProjectCode).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)),
            string.Join(",", plans.Select(p => p.Currency).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)),
            currency,
            Math.Round(plans.Where(p => p.Disbursed is not null).Sum(p => p.Disbursed!.Value), 2));
    }

    // ---------------------------------------------------------------- views

    private static object DescribeSummary(ExpenseReportSummary r) => new
    {
        report = r.Number,
        name = r.Name,
        status = ReportStatusName(r.Status),
        dates = r.EarliestDate == r.LatestDate ? r.EarliestDate : $"{r.EarliestDate}..{r.LatestDate}",
        card_count = r.CardCount,
        total = r.Total,
        reimbursement = r.Reimbursement == r.Total ? null : r.Reimbursement,
        currency = r.Currency,
        projects = r.Projects.Select(p => p.Code).OfType<string>().ToList(),
        editable = !r.Locked
    };

    private static object DescribeReport(ExpenseReportDetail d, string? query, IReadOnlyList<ExpenseReceiptRule>? rules)
    {
        var receiptsByCard = d.Receipts
            .SelectMany(r => r.CardUids.Select(uid => (uid, r)))
            .GroupBy(x => x.uid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.r).ToList(), StringComparer.Ordinal);
        var cards = d.Cards
            .Where(c => query is null || TextMatch.Matches(query, c.Description, c.ExpenseType, c.ProjectCode, c.ProjectName))
            .OrderBy(c => c.Date, StringComparer.Ordinal)
            .ThenBy(c => c.ExpenseType, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                card_uid = c.Uid,
                date = c.Date,
                expense_type = c.ExpenseType,
                description = c.Description,
                amount = c.Amount,
                currency = c.Currency,
                // Projector stores how many incurred units one report-currency unit costs; the web shows 1 / FxRate.
                rate = c.FxRate is > 0 && c.Currency != d.Currency ? Math.Round(1 / c.FxRate.Value, 8) : (double?)null,
                amount_report_currency = c.DisbursedAmount,
                location = c.Location,
                project_code = c.ProjectCode,
                status = CardStatusName(c.ApprovalStatus),
                editable = c.Editable && d.Editable,
                rejected_reason = c.RejectedReason,
                receipts = c.Uid is not null && receiptsByCard.TryGetValue(c.Uid, out var list)
                    ? list.Select(r => r.Name).ToList()
                    : null,
                // Projector refuses to submit it until a receipt is added.
                missing_receipt = c.DocumentCount == 0 && (c.Uid is null || !receiptsByCard.ContainsKey(c.Uid))
                    && rules?.FirstOrDefault(r => string.Equals(r.Name, c.ExpenseType, StringComparison.OrdinalIgnoreCase))
                        ?.AppliesTo(c.DisbursedAmount) == true
                    ? true
                    : (bool?)null
            })
            .ToList();
        return new
        {
            report = d.Number,
            name = d.Name,
            status = ReportStatusName(d.Status),
            person = d.ResourceName,
            currency = d.Currency,
            total = d.Total,
            card_count = d.Cards.Count,
            shown = cards.Count,
            editable = d.Editable,
            locked_reason = d.Editable ? null : DescribeLock(d),
            cards,
            report_receipts = d.Receipts.Where(r => r.EntireReport).Select(r => r.Name).ToList() is { Count: > 0 } whole ? whole : null
        };
    }

    private static string DescribeLock(ExpenseReportDetail d) => d.MaintainUnavailableReason switch
    {
        "LOK" => "all its cards are approved",
        "PRM" => "you lack permission",
        _ => d.Locked ? "locked" : "not editable"
    };

    internal static string? ReportStatusName(string? code) =>
        code is null ? null : ReportStatusNames.TryGetValue(code, out var name) ? name : code;

    internal static string? CardStatusName(string? code) =>
        code is null ? null : CardStatusNames.TryGetValue(code, out var name) ? name : code;

    // ---------------------------------------------------------------- lookups (cached per user)

    private async Task<ExpenseIdentity> RequireSelfAsync(ProjectorConnection connection, CancellationToken ct)
    {
        if (_cache.TryGet<ExpenseIdentity>(connection, SelfKind, string.Empty, out var cached) && cached is not null)
        {
            return cached;
        }

        var self = await WithRefreshAsync(connection, c => _expenses.FindSelfAsync(c, ct), ct)
            ?? throw new ProjectorApiException(
                "Projector does not tell this assistant who you are until you have at least one expense report. " +
                "Create your first expense report in Projector, then try again.",
                "NoExpenseIdentity");
        _cache.Set(connection, SelfKind, string.Empty, self, TimeEntryCache.RulesTtl);
        return self;
    }

    private Task<ExpenseEntryRules> GetRulesAsync(ProjectorConnection connection, CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, RulesKind, string.Empty, TimeEntryCache.RulesTtl, () =>
            WithRefreshAsync(connection, c => _expenses.GetEntryRulesAsync(c, ct), ct));

    private Task<ExpenseEntryInfo> GetEntryInfoAsync(
        ProjectorConnection connection, string resourceId, string start, string end, CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, EntryKind, $"{resourceId}|{start}|{end}", TimeEntryCache.LookupTtl, () =>
            WithRefreshAsync(connection, c => _expenses.GetEntryInfoAsync(c, resourceId, start, end, ct), ct));

    private async Task<string?> GetReportCurrencyAsync(ProjectorConnection connection, string resourceId, CancellationToken ct)
    {
        var box = await _cache.GetOrLoadAsync(connection, CurrencyKind, resourceId, TimeEntryCache.RulesTtl, async () =>
            new StrongBox(await WithRefreshAsync(connection, c => _expenses.GetNewReportAsync(c, resourceId, ct), ct) is { } skeleton
                ? skeleton.Currency
                : null));
        return box.Value;
    }

    private Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(
        ProjectorConnection connection, string resourceId, string currency, string date, CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, RatesKind, $"{currency}|{date}", TimeEntryCache.RulesTtl, () =>
            WithRefreshAsync(connection, c => _expenses.GetCurrenciesAsync(c, resourceId, currency, date, ct), ct));

    /// <summary>The receipt rules per expense type; null when Projector does not return them (the check is then skipped).</summary>
    private async Task<IReadOnlyList<ExpenseReceiptRule>?> TryGetReceiptRulesAsync(
        ProjectorConnection connection, string resourceId, string currency, CancellationToken ct)
    {
        try
        {
            return await _cache.GetOrLoadAsync(connection, ReceiptRulesKind, $"{resourceId}|{currency}", TimeEntryCache.RulesTtl, () =>
                WithRefreshAsync(connection, c => _expenses.GetReceiptRulesAsync(c, resourceId, currency, Today(), ct), ct));
        }
        catch (ProjectorApiException ex)
        {
            _logger.LogWarning(ex, "Expense receipt rules could not be read ({ErrorCode})", ex.ErrorCode);
            return null;
        }
    }

    /// <summary>true, "from N CUR" (a threshold), false, or null when unknown.</summary>
    private static object? ReceiptRequirement(IReadOnlyList<ExpenseReceiptRule>? rules, string? type, string? currency)
    {
        var rule = rules?.FirstOrDefault(r => string.Equals(r.Name, type, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return null;
        }

        return !rule.Required ? false
            : rule.Threshold is > 0 ? string.Create(CultureInfo.InvariantCulture, $"from {rule.Threshold} {currency}")
            : true;
    }

    private Task<ReceiptPool> GetPoolAsync(ProjectorConnection connection, string userUid, CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, PoolKind + "_folder", userUid, TimeEntryCache.RulesTtl, () =>
            WithRefreshAsync(connection, c => _expenses.GetReceiptPoolAsync(c, userUid, ct), ct));

    private sealed class StrongBox(string? value)
    {
        public string? Value { get; } = value;
    }

    // ---------------------------------------------------------------- helpers

    internal static ProjectorApiException MapSaveError(ProjectorApiException ex)
    {
        if (string.Equals(ex.ErrorCode, "UpdatePermissionDenied", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectorApiException(
                TimeEntryToolService.WebServicesAccessViewOnlyMessage.Replace("time cards", "expenses", StringComparison.Ordinal),
                TimeEntryToolService.WebServicesAccessViewOnly,
                ex);
        }

        return ex.ErrorCode switch
        {
            "TimestampMismatch" or "ExpenseDocumentHasBeenChanged" or "ConcurrencyViolation" =>
                new ProjectorApiException("The report changed in Projector since it was read. Read it again with list_expenses and retry.", ex.ErrorCode, ex),
            _ => ex
        };
    }

    private static long ReceiptMaxBytes(ExpenseEntryRules rules) =>
        rules.ReceiptMaxBytes > 0 ? rules.ReceiptMaxBytes : DefaultReceiptMaxBytes;

    private static byte[]? TryDecode(string base64)
    {
        var text = base64.Trim();
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            text = text[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? Kb(long? bytes) => bytes is null ? null : Math.Round(bytes.Value / 1024.0, 1);

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var extension = Path.GetExtension(text);
        return text[..(max - extension.Length)] + extension;
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string AddDays(string date, int days) =>
        DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? TryParseDate(string? value)
    {
        var raw = value?.Trim();
        return raw is not null && DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? raw
            : null;
    }

    private static string ParseDate(string value, string name) =>
        TryParseDate(value) ?? throw new ArgumentException($"{name} must be a date in yyyy-MM-dd format.");

    private Task<ProjectorConnection> RequireAsync(string connectionId, CancellationToken ct) =>
        _connections.RequireConnectionAsync(connectionId, requiredScope: null, ct);

    // Same refresh-once rule as the other tool services: an invalid session means Projector rejected the call
    // before doing anything, so re-running it (including a save) cannot duplicate work.
    private async Task<T> WithRefreshAsync<T>(
        ProjectorConnection connection,
        Func<ProjectorConnection, Task<T>> action,
        CancellationToken ct)
    {
        try
        {
            return await action(connection);
        }
        catch (ProjectorApiException ex) when (IsAuthFailure(ex))
        {
            _cache.ClearUser(connection);
            connection = await _connections.RefreshConnectionAsync(connection, ct);
            return await action(connection);
        }
    }

    private static bool IsAuthFailure(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
}
