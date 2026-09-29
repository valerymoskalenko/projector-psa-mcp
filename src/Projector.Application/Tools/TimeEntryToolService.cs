using System.Globalization;
using Microsoft.Extensions.Logging;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Timecards;

namespace Projector.Application.Tools;

/// <summary>Input for <see cref="TimeEntryToolService.SaveTimecardAsync"/> as the MCP tool receives it.</summary>
public sealed record SaveTimecardInput(
    string WorkDate,
    double Hours,
    string ProjectCode,
    string Task,
    string Role,
    string Narrative,
    string? TimecardUid = null,
    string? Location = null,
    string? Udf1 = null,
    string? Udf2 = null);

/// <summary>
/// Time entry for the signed-in user: find chargeable projects, one project's options, and save one work card.
/// Every call targets the caller's own time sheet (no resource parameter). Saves never submit: a new card is
/// Draft, and an update is allowed only for the caller's Draft or Rejected cards.
/// </summary>
public sealed class TimeEntryToolService
{
    /// <summary>Error code returned to the agent when Projector refuses a save because the user has Web Services Access V.</summary>
    public const string WebServicesAccessViewOnly = "web_services_access_view_only";

    /// <summary>
    /// Written for the agent to relay as is. Projector answers any save by a user with Web Services Access V
    /// with UpdatePermissionDenied ("You do not have permission to update this item.").
    /// </summary>
    public const string WebServicesAccessViewOnlyMessage =
        "Nothing was saved. Your Projector user has Web Services Access V (View), not U (Update), so time cards " +
        "cannot be created or changed through this assistant. Please ask your Projector PSA administrator to change " +
        "your Web Services Access permission from V (View) to U (Update), then try again. " +
        "Do not retry this call until the permission is changed.";

    public const int MaxNarrativeLength = 1000;
    public const int MaxCardsPerSave = 20;

    /// <summary>Error code of a save whose outcome is unknown (the request may have reached Projector).</summary>
    private const string WriteOutcomeUnknown = "write_outcome_unknown";
    public const int DefaultMaxTasks = 50;
    public const int MaxTasksLimit = 200;
    private const int MaxListedNames = 25;
    private const string RulesKind = "rules";
    private const string ProjectsKind = "projects";
    private const string SetupKind = "setup";
    private const string DayCardsKind = "day_cards";
    private const string AssignmentsKind = "assignments";
    /// <summary>
    /// Microsoft 365 Copilot Chat lists only a connector's read tools (seen 2026-09-29): the model saw "call
    /// save_timecard" without the tool and told the user the server can't save.
    /// </summary>
    public const string NoSaveToolHint =
        "If save_timecard is not among your tools, this client offers only read tools (Microsoft 365 Copilot Chat does " +
        "until write actions roll out): tell the user this chat can't save, suggest the Copilot Cowork tab or the Projector " +
        "PSA agent, and don't say the Projector server can't save.";

    private const string RecentKind = "recent";
    private const int RecentDays = 30;
    private const int MaxRecentTasks = 5;
    private static readonly HashSet<string> EditableStatuses = new(StringComparer.OrdinalIgnoreCase) { "D", "R" };

    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorTimeEntryClient _timeEntry;
    private readonly TimeEntryCache _cache;
    private readonly ILogger<TimeEntryToolService> _logger;

    public TimeEntryToolService(
        ProjectorConnectionService connections,
        IProjectorTimeEntryClient timeEntry,
        TimeEntryCache cache,
        ILogger<TimeEntryToolService> logger)
    {
        _connections = connections;
        _timeEntry = timeEntry;
        _cache = cache;
        _logger = logger;
    }

    public async Task<object> ListTimeProjectsAsync(
        string connectionId,
        string workDate,
        string? query,
        int maxRows,
        CancellationToken ct,
        int offset = 0,
        bool chargeableOnly = true)
    {
        var date = ParseWorkDate(workDate);
        maxRows = Math.Clamp(maxRows, 1, 200);
        offset = Math.Max(0, offset);
        var connection = await RequireAsync(connectionId, ct);
        var all = await GetProjectsAsync(connection, date, ct);
        var (recent, recentNote) = await TryGetRecentUsageAsync(connection, date, ct);
        RecentUsage? Usage(TimeEntryProjectSummary p) => recent?.GetValueOrDefault(p.ProjectCode.ToUpperInvariant());

        // Filtered here, on the cached full list (Projector ignores MaximumRows anyway), so a new query or page
        // does not call Projector again. By default only projects where the user has a role: time elsewhere is refused.
        // A query also matches the tasks the user posted to recently (task path, WBS or the cards' descriptions), so
        // an opportunity or customer name finds the project and task used for that work.
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        IReadOnlyList<RecentTask> MatchedTasks(TimeEntryProjectSummary p) =>
            text is null || Usage(p) is not { } u
                ? []
                : u.Tasks.Where(task => TextMatch.Matches(text, [task.Path, task.WbsCode, .. task.Descriptions])).ToList();
        int Score(TimeEntryProjectSummary p) => Math.Max(
            TextMatch.Score(text!, p.ProjectCode, p.ProjectName, p.EngagementCode, p.EngagementName, p.ClientName),
            MatchedTasks(p).Count > 0 ? 1 : 0);

        // Most recently used first, then Projector's order.
        var candidates = all
            .Where(p => !chargeableOnly || p.Roles.Count > 0)
            .Select((p, i) => (p, i))
            .OrderByDescending(x => Usage(x.p)?.LastUsed ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.i)
            .Select(x => x.p)
            .ToList();
        var notChargeableHidden = all.Count - candidates.Count;
        var matches = text is null ? candidates : Ranked(candidates, Score);
        var hiddenMatches = chargeableOnly && text is not null && matches.Count == 0
            ? all.Count(p => p.Roles.Count == 0 && Score(p) > 0)
            : 0;
        var page = matches.Skip(offset).Take(maxRows).ToList();
        var hasMore = offset + page.Count < matches.Count;
        return new
        {
            work_date = date,
            query = text,
            count = page.Count,
            total = matches.Count,
            offset,
            has_more = hasMore,
            next_offset = hasMore ? offset + page.Count : (int?)null,
            not_chargeable_hidden = chargeableOnly ? notChargeableHidden : (int?)null,
            projects = page.Select(p => new
            {
                project_code = p.ProjectCode,
                project_name = p.ProjectName,
                engagement_code = p.EngagementCode,
                engagement_name = p.EngagementName,
                client_name = p.ClientName,
                billable = p.Billable,
                chargeable = p.Roles.Count > 0,
                roles = p.Roles.Select(r => new { role_uid = r.Uid, role_name = r.Name }).ToList(),
                unavailable_reason = p.UnavailableReasonCode,
                last_used = Usage(p)?.LastUsed,
                hours_last_30d = Usage(p)?.Hours,
                recent_tasks = Usage(p)?.Tasks.Take(MaxRecentTasks).Select(TaskView).ToList(),
                matched_tasks = MatchedTasks(p) is { Count: > 0 } matched ? matched.Select(TaskView).ToList() : null
            }).ToList(),
            recent_note = recentNote,
            next_step = hiddenMatches > 0
                ? $"{hiddenMatches} project(s) match '{text}' but you have no role there, so Projector refuses time on them " +
                  "(see them with chargeable_only = false). Ask the project manager to add you, or pick another project."
                : text is not null && matches.Count == 0
                ? $"No project you can enter time on matches '{text}'. Projector lists only projects where you have a role: " +
                  "check the name with list_engagements, and ask the project manager to add you if it exists. The words may " +
                  "also be a task inside another project that you haven't posted to in the last 30 days: find it with " +
                  "list_timecards (query, a longer date range) or get_timecard_options (query) on the likely project."
                : "Call get_timecard_options with a project_code where chargeable is true and the same work_date " +
                  "(add query to find a task by name, WBS or parent)."
        };
    }

    /// <summary>The user's own posting on one project in the recent window.</summary>
    private sealed record RecentUsage(string LastUsed, double Hours, IReadOnlyList<RecentTask> Tasks);

    /// <summary>A task the user posted to recently; <paramref name="Descriptions"/> are only matched, never shown.</summary>
    private sealed record RecentTask(string? Path, string? WbsCode, double Hours, string LastUsed, IReadOnlyList<string> Descriptions);

    private static object TaskView(RecentTask t) =>
        new { task_path = t.Path, wbs_code = t.WbsCode, hours = t.Hours, last_used = t.LastUsed };

    /// <summary>
    /// The user's own cards of the last <see cref="RecentDays"/> days up to the work date, summed per project and task
    /// (one Projector read, cached per user). A failed read never blocks the project list: it returns a note instead.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, RecentUsage>? Usage, string? Note)> TryGetRecentUsageAsync(
        ProjectorConnection connection,
        string workDate,
        CancellationToken ct)
    {
        var end = DateTime.ParseExact(workDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = end.AddDays(-(RecentDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var cards = await _cache.GetOrLoadAsync(connection, RecentKind, workDate, TimeEntryCache.LookupTtl, () =>
                WithRefreshAsync(connection, c => _timeEntry.ListOwnTimecardsAsync(c, start, workDate, ct), ct));
            var usage = cards
                .Where(c => !string.IsNullOrWhiteSpace(c.ProjectCode) && !string.IsNullOrWhiteSpace(c.WorkDate))
                .GroupBy(c => c.ProjectCode!.ToUpperInvariant())
                .ToDictionary(
                    g => g.Key,
                    g => new RecentUsage(
                        g.Max(c => c.WorkDate!)!,
                        g.Sum(c => c.WorkMinutes) / 60.0,
                        g.GroupBy(c => c.ProjectTaskUid ?? c.TaskWbsCode ?? c.TaskName ?? string.Empty)
                            .Select(tg => new RecentTask(
                                tg.First().TaskPath ?? tg.First().TaskName,
                                tg.First().TaskWbsCode,
                                tg.Sum(c => c.WorkMinutes) / 60.0,
                                tg.Max(c => c.WorkDate!)!,
                                tg.Select(c => c.Description).OfType<string>().Distinct(StringComparer.Ordinal).ToList()))
                            .OrderByDescending(task => task.LastUsed, StringComparer.Ordinal)
                            .ThenByDescending(task => task.Hours)
                            .ToList()),
                    StringComparer.Ordinal);
            return (usage, null);
        }
        catch (Exception ex) when (ex is ProjectorApiException or HttpRequestException or TaskCanceledException
            && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "list_time_projects: could not read recent cards from {Start} to {End}", start, workDate);
            return (null, $"Recent usage is missing: your cards from {start} to {workDate} could not be read. " +
                "list_timecards shows them.");
        }
    }

    public async Task<object> GetTimecardOptionsAsync(
        string connectionId,
        string projectCode,
        string workDate,
        CancellationToken ct,
        string? query = null,
        int maxTasks = DefaultMaxTasks,
        int offset = 0)
    {
        var date = ParseWorkDate(workDate);
        var code = RequireText(projectCode, "project_code");
        maxTasks = Math.Clamp(maxTasks, 1, MaxTasksLimit);
        offset = Math.Max(0, offset);
        var connection = await RequireAsync(connectionId, ct);

        var setup = await GetSetupAsync(connection, code, date, ct) ?? throw ProjectNotFound(code, date);
        var roles = await GetRolesAsync(connection, code, date, ct);
        var rules = await GetRulesAsync(connection, ct);

        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        // Summary tasks report OpenForTimeFlag=true but Projector rejects their time at submit: never offer them.
        var openTasks = setup.Tasks.Where(t => t.AcceptsTime).ToList();
        var summaryHidden = setup.Tasks.Count(t => t.OpenForTime && t.HasChildren);
        var matches = text is null
            ? openTasks
            : Ranked(openTasks, t => TextMatch.Score(text, t.WbsCode, t.Name, t.Path));
        var page = matches.Skip(offset).Take(maxTasks).ToList();
        var hasMore = offset + page.Count < matches.Count;

        var (assignments, assignmentNote) = await TryGetAssignmentsAsync(connection, setup, ct);
        var roleUids = roles.Select(r => r.Uid).ToList();
        bool? Assigned(TimeEntryTask t) => assignments?.Restricted == true ? assignments.IsAssigned(t.Uid, roleUids) : null;

        // Most tasks share one set of rate types: list it once, and on a task only when that task differs.
        var commonRateTypes = CommonRateTypes(openTasks, setup);
        return new
        {
            work_date = date,
            project = new
            {
                project_code = setup.ProjectCode,
                project_name = setup.ProjectName,
                engagement_code = setup.EngagementCode,
                client_name = setup.ClientName,
                billable = setup.Billable,
                open_for_time = setup.OpenForTime,
                narrative_required = setup.DescriptionRequired
            },
            roles = roles.Select(r => new { role_uid = r.Uid, role_name = r.Name, start_date = r.StartDate, end_date = r.EndDate }).ToList(),
            rate_types = commonRateTypes?.Select(r => new { rate_type_uid = r.Uid, rate_type_name = r.Name }).ToList(),
            task_query = text,
            tasks = page.Select(t => new
            {
                task_uid = t.Uid,
                task_name = t.Name,
                task_path = t.Path != t.Name ? t.Path : null,
                wbs_code = t.WbsCode,
                task_type = t.TaskTypeName,
                rate_types = SameRateTypes(RateTypesFor(t, setup), commonRateTypes)
                    ? null
                    : RateTypesFor(t, setup).Select(r => new { rate_type_uid = r.Uid, rate_type_name = r.Name }).ToList(),
                default_rate_type = TryDefaultRateType(t, setup)?.Name,
                assigned = Assigned(t)
            }).ToList(),
            tasks_count = page.Count,
            tasks_total = matches.Count,
            tasks_offset = offset,
            tasks_has_more = hasMore,
            tasks_next_offset = hasMore ? offset + page.Count : (int?)null,
            tasks_open_count = openTasks.Count,
            tasks_closed_count = setup.Tasks.Count - openTasks.Count - summaryHidden,
            tasks_summary_hidden = summaryHidden,
            assignment_note = assignmentNote,
            rules = new
            {
                time_increment_minutes = rules.ReportingTimeIncrementMinutes,
                max_hours_per_day = rules.Enforce24HourDailyLimit ? 24 : (int?)null,
                location_required = rules.RequireLocation,
                udf1 = DescribeUdf(rules.Udf1, setup.Udf1Treatment),
                udf2 = DescribeUdf(rules.Udf2, setup.Udf2Treatment)
            },
            rate_type_note = "Rate types are listed for information only: save_timecard always uses the task's default_rate_type.",
            next_step = text is not null && matches.Count == 0
                ? $"No task matches '{text}' among the {openTasks.Count} tasks that take time here. Try one word, the parent " +
                  "task's name or the WBS code; to find the task used before for this work, call list_timecards with " +
                  "project_code and query."
                : hasMore
                    ? "More tasks match: narrow with query (task name, WBS or parent) or page with offset = tasks_next_offset."
                    : "Confirm the card with the user (date, hours, project, task path, role, narrative), then call save_timecard. " +
                      NoSaveToolHint
        };
    }

    /// <summary>
    /// Saves up to <see cref="MaxCardsPerSave"/> cards in one call (one user approval). Every card is checked first;
    /// invalid cards are reported and not sent, valid ones are saved one by one (one Projector call each, never
    /// retried). A Projector error on one card doesn't stop the others, but an unknown outcome (the save may or may
    /// not have happened) stops the batch: the rest are reported as not attempted, so nothing is sent twice.
    /// With <paramref name="dryRun"/> nothing is saved: the result shows what would be saved and the day totals.
    /// </summary>
    public async Task<object> SaveTimecardsAsync(
        string connectionId,
        IReadOnlyList<SaveTimecardInput>? cards,
        bool dryRun,
        CancellationToken ct)
    {
        var outcome = await SaveCardsCoreAsync(connectionId, cards, dryRun, ct);
        var results = outcome.Cards.Select(c => c.View).ToList();
        int Count(string status) => outcome.Cards.Count(c => c.Status == status);
        LogAudit(outcome, dryRun);
        return new
        {
            action = dryRun ? "dry_run" : "saved",
            results,
            days = outcome.Days,
            saved_count = Count("saved"),
            valid_count = dryRun ? Count("valid") : (int?)null,
            invalid_count = Count("invalid"),
            failed_count = Count("failed"),
            not_attempted_count = Count("not_attempted"),
            submitted = false,
            note = dryRun
                ? "Dry run: nothing was saved. Cards with status valid would be saved; fix the invalid ones first."
                : "Saved cards are Drafts, not submitted. Submit your time sheet in Projector when it is complete.",
            warnings = outcome.Warnings.Count == 0 ? null : outcome.Warnings
        };
    }

    /// <summary>
    /// One card, the shape tests use: returns that card's result, or throws its error (same exception as before
    /// batches existed).
    /// </summary>
    internal async Task<object> SaveTimecardAsync(
        string connectionId,
        SaveTimecardInput input,
        CancellationToken ct)
    {
        var outcome = await SaveCardsCoreAsync(connectionId, [input], dryRun: false, ct);
        var card = outcome.Cards[0];
        if (card.Error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(card.Error).Throw();
        }

        return card.View;
    }

    /// <param name="ErrorCode">Why an invalid or failed card was not saved.</param>
    /// <param name="WarningKinds">Short codes of the card's warnings, for the audit log.</param>
    private sealed record CardOutcome(
        int Index,
        string Status,
        object View,
        Exception? Error = null,
        string? ErrorCode = null,
        IReadOnlyList<string>? WarningKinds = null);

    /// <summary>
    /// One line per save_timecard call for issue resolution: counts, error and warning codes, project codes, dates
    /// and day totals. No narratives, task names or other card contents.
    /// </summary>
    private void LogAudit(BatchOutcome outcome, bool dryRun)
    {
        int Count(string status) => outcome.Cards.Count(c => c.Status == status);
        static string Codes(IEnumerable<string> codes) =>
            string.Join(",", codes.GroupBy(c => c, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}x{g.Count()}"));

        _logger.LogInformation(
            "save_timecard audit: {SaveAction} {CardCount} card(s): {Saved} saved, {Valid} valid, {Invalid} invalid, " +
            "{Failed} failed, {NotAttempted} not attempted; errors [{ErrorCodes}]; warnings [{WarningCodes}]; " +
            "projects [{ProjectCodes}]; days [{DayTotals}]",
            dryRun ? "dry_run" : "save",
            outcome.Cards.Count,
            Count("saved"),
            Count("valid"),
            Count("invalid"),
            Count("failed"),
            Count("not_attempted"),
            Codes(outcome.Cards.Select(c => c.ErrorCode).OfType<string>()),
            Codes(outcome.Cards.SelectMany(c => c.WarningKinds ?? [])),
            string.Join(",", outcome.ProjectCodes),
            string.Join(",", outcome.DayTotals.Select(d =>
                string.Create(CultureInfo.InvariantCulture, $"{d.Date}={d.Hours:0.##}h/{d.Cards}"))));
    }

    private sealed record BatchOutcome(
        IReadOnlyList<CardOutcome> Cards,
        IReadOnlyList<object> Days,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> ProjectCodes,
        IReadOnlyList<(string Date, double Hours, int Cards)> DayTotals);

    /// <summary>A card that passed every check: the Projector request plus what the result shows.</summary>
    private sealed record PreparedCard(
        int Index,
        TimecardSaveRequest Request,
        TimeEntryProjectSetup Setup,
        TimeEntryTask Task,
        TimeEntryRole Role,
        TimeEntryRateType RateType,
        OwnTimecard? Existing)
    {
        public bool IsUpdate => Request.TimecardUid is not null;
    }

    private async Task<BatchOutcome> SaveCardsCoreAsync(
        string connectionId,
        IReadOnlyList<SaveTimecardInput>? cards,
        bool dryRun,
        CancellationToken ct)
    {
        if (cards is null || cards.Count is 0 or > MaxCardsPerSave)
        {
            throw new ArgumentException($"cards must contain 1–{MaxCardsPerSave} time cards.");
        }

        var connection = await RequireAsync(connectionId, ct);
        var rules = await GetRulesAsync(connection, ct);
        var outcomes = new CardOutcome?[cards.Count];
        var prepared = new List<PreparedCard>();
        for (var i = 0; i < cards.Count; i++)
        {
            try
            {
                prepared.Add(await PrepareAsync(connection, i, cards[i], rules, ct));
            }
            catch (Exception ex) when (IsCardError(ex, ct))
            {
                outcomes[i] = Failed(i, "invalid", ex);
            }
        }

        // The day's cards feed the duplicate check and the day totals; earlier cards of this batch are added as they
        // are saved (or, in a dry run, as they would be), so a duplicate inside the batch is caught too.
        var days = new Dictionary<string, List<Timecard>?>(StringComparer.Ordinal);
        var unreadDates = new HashSet<string>(StringComparer.Ordinal);
        // The cards that were there before this call: the role check compares against these only, so one wrong role
        // earlier in the batch can't flag the right role on the cards after it.
        var priorCards = new Dictionary<string, IReadOnlyList<Timecard>>(StringComparer.Ordinal);
        foreach (var date in prepared.Select(p => p.Request.WorkDate).Distinct(StringComparer.Ordinal))
        {
            var dayCards = await TryGetDayCardsAsync(connection, date, ct);
            days[date] = dayCards?.ToList();
            priorCards[date] = PriorCards(connection, date, dayCards);
            if (dayCards is null)
            {
                unreadDates.Add(date);
            }
        }

        var stopped = false;
        foreach (var card in prepared)
        {
            var date = card.Request.WorkDate;
            if (stopped)
            {
                outcomes[card.Index] = new CardOutcome(card.Index, "not_attempted", new
                {
                    index = card.Index,
                    status = "not_attempted",
                    message = "Not sent: an earlier save in this call had an unknown outcome. Check list_timecards, then send this card again."
                });
                continue;
            }

            var cardWarnings = new List<string>();
            var warningKinds = new List<string>();
            if (unreadDates.Contains(date))
            {
                cardWarnings.Add($"Could not read your other cards for {date}, so there is no duplicate check or day total. " +
                    "Check with list_timecards.");
                warningKinds.Add("day_unread");
            }

            if (!card.IsUpdate && days[date] is { } before)
            {
                if (FindLikelyDuplicate(before, card.Request.ProjectCode, card.Task.Uid, card.Request.WorkMinutes, card.Request.Description) is { } duplicate)
                {
                    cardWarnings.Add(
                        $"Possible duplicate: card {duplicate.TimecardUid ?? "earlier in this call"} ({duplicate.WorkHours:0.##} h, " +
                        $"{duplicate.Status}) is already on {date} for the same project and task: \"{Shorten(duplicate.Description)}\". " +
                        "Both cards are kept; ask the user whether this one should stay.");
                    warningKinds.Add("duplicate");
                }
                else if (FindSimilarOnOtherTask(before, card.Request.ProjectCode, card.Task.Uid, card.Request.Description) is { } other)
                {
                    cardWarnings.Add(
                        $"Possible duplicate on another task: card {other.TimecardUid ?? "earlier in this call"} ({other.WorkHours:0.##} h, " +
                        $"{other.Status}) on {date} has a similar narrative under {other.ProjectCode} > {other.TaskPath ?? other.TaskName} " +
                        $"(WBS {other.TaskWbsCode}): \"{Shorten(other.Description)}\". Both cards are kept; ask the user whether the " +
                        "same work was entered twice.");
                    warningKinds.Add("duplicate_other_task");
                }
            }

            if (RoleWarning(card, priorCards.GetValueOrDefault(date) ?? []) is { } roleWarning)
            {
                cardWarnings.Add(roleWarning);
                warningKinds.Add("role_differs");
            }

            if (dryRun)
            {
                AddToDay(days, date, ToTimecard(card, card.Request.TimecardUid, "D"));
                outcomes[card.Index] = new CardOutcome(card.Index, "valid", CardView(card, "valid", card.Request.TimecardUid, "D", DayView(days, date), cardWarnings, note: null), WarningKinds: warningKinds);
                continue;
            }

            TimecardSaveResult saved;
            try
            {
                saved = await WithRefreshAsync(connection, c => _timeEntry.SaveTimecardAsync(c, card.Request, ct), ct);
            }
            catch (ProjectorApiException ex)
            {
                _logger.LogWarning(
                    "save_timecard failed: {ErrorCode} {ErrorMessage} (update={IsUpdate})", ex.ErrorCode, ex.Message, card.IsUpdate);
                _cache.Remove(connection, DayCardsKind, date);
                days[date] = null;
                var mapped = MapSaveError(ex);
                outcomes[card.Index] = Failed(card.Index, "failed", mapped);
                stopped = string.Equals(ex.ErrorCode, WriteOutcomeUnknown, StringComparison.Ordinal);
                continue;
            }

            // Projector saves every create and every update as Draft: a Rejected card goes back to Draft when saved
            // (seen live 2026-09-25), and the user resubmits it like any other draft.
            const string expectedStatus = "D";
            if (saved.SubmittedFlag)
            {
                _logger.LogError("save_timecard: Projector reported SubmittedFlag=true for card {TimecardUid}", saved.TimecardUid);
                cardWarnings.Add("Projector reported the card as submitted, which save_timecard never requests. Check it in Projector.");
                warningKinds.Add("submitted_flag");
            }

            if (!string.IsNullOrWhiteSpace(saved.CardStatusCode)
                && !string.Equals(saved.CardStatusCode, expectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "save_timecard: card {TimecardUid} status {Actual}, expected {Expected}", saved.TimecardUid, saved.CardStatusCode, expectedStatus);
                cardWarnings.Add($"The card is now {StatusName(saved.CardStatusCode)} (expected {StatusName(expectedStatus)}).");
                warningKinds.Add("unexpected_status");
            }

            var statusCode = saved.CardStatusCode ?? expectedStatus;
            var savedUid = saved.TimecardUid ?? card.Request.TimecardUid;
            if (days[date] is not null && savedUid is not null)
            {
                AddToDay(days, date, ToTimecard(card, savedUid, statusCode));
                _cache.Set(connection, DayCardsKind, date, (IReadOnlyList<Timecard>)days[date]!, TimeEntryCache.DayCardsTtl);
            }
            else
            {
                _cache.Remove(connection, DayCardsKind, date);
            }

            var wasRejected = card.IsUpdate && string.Equals(card.Existing!.CardStatusCode, "R", StringComparison.OrdinalIgnoreCase);
            outcomes[card.Index] = new CardOutcome(card.Index, "saved", CardView(
                card, "saved", savedUid, statusCode, DayView(days, date), cardWarnings,
                wasRejected
                    ? "Saved. The card was Rejected and is now a Draft again, not submitted. Resubmit it in Projector."
                    : "Saved as Draft, not submitted. Submit your time sheet in Projector when it is complete."),
                WarningKinds: warningKinds);
        }

        var dayTotals = days.Keys.OrderBy(d => d, StringComparer.Ordinal).Select(d => DayView(days, d)).OfType<object>().ToList();
        var auditDays = days.Where(d => d.Value is not null)
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => (d.Key, d.Value!.Sum(c => c.WorkMinutes) / 60.0, d.Value!.Count))
            .ToList();
        var projectCodes = prepared.Select(p => p.Setup.ProjectCode).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new BatchOutcome(outcomes.Select(o => o!).ToList(), dayTotals, [], projectCodes, auditDays);
    }

    /// <summary>Every check a card needs before it may be sent; throws with the reason when it can't be saved.</summary>
    private async Task<PreparedCard> PrepareAsync(
        ProjectorConnection connection,
        int index,
        SaveTimecardInput input,
        TimeEntryParameters rules,
        CancellationToken ct)
    {
        var date = ParseWorkDate(input.WorkDate);
        var projectCode = RequireText(input.ProjectCode, "project_code");
        var taskInput = RequireText(input.Task, "task");
        var roleInput = RequireText(input.Role, "role");
        var narrative = RequireText(input.Narrative, "narrative");
        if (narrative.Length > MaxNarrativeLength)
        {
            throw new ArgumentException($"narrative is {narrative.Length} characters; Projector allows at most {MaxNarrativeLength}.");
        }

        if (double.IsNaN(input.Hours) || input.Hours <= 0 || input.Hours > 24)
        {
            throw new ArgumentException("hours must be more than 0 and at most 24.");
        }

        var timecardUid = string.IsNullOrWhiteSpace(input.TimecardUid) ? null : input.TimecardUid.Trim();
        var minutes = ToMinutes(input.Hours, rules.ReportingTimeIncrementMinutes);

        OwnTimecard? existing = null;
        if (timecardUid is not null)
        {
            // Never cached: the Timestamp must be current or Projector rejects the update.
            existing = await WithRefreshAsync(connection, c => _timeEntry.GetOwnTimecardAsync(c, timecardUid, date, ct), ct)
                ?? throw new ProjectorApiException(
                    $"No time card {timecardUid} dated {date} on your own time sheet. Use the timecardUid and workDate " +
                    "from list_timecards; you can only update your own cards.",
                    "timecard_not_found");

            if (!EditableStatuses.Contains(existing.CardStatusCode ?? string.Empty))
            {
                throw new ProjectorApiException(
                    $"Time card {timecardUid} is {StatusName(existing.CardStatusCode)}. save_timecard only changes Draft or " +
                    "Rejected cards; change this card in Projector.",
                    "timecard_not_editable");
            }

            if (!string.Equals(existing.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectorApiException(
                    $"Time card {timecardUid} is on project {existing.ProjectCode}. save_timecard cannot move a card to " +
                    "another project or date; create a new card instead.",
                    "timecard_move_not_supported");
            }
        }

        var setup = await GetSetupAsync(connection, projectCode, date, ct) ?? throw ProjectNotFound(projectCode, date);
        if (!setup.OpenForTime)
        {
            throw new ProjectorApiException(
                $"Project {projectCode} is not open for time entry on {date}.", "project_closed");
        }

        var task = ResolveTask(setup.Tasks, taskInput, projectCode);
        if (!task.OpenForTime)
        {
            throw new ProjectorApiException(
                $"Task '{task.Path ?? task.Name}' on project {projectCode} is not open for time entry.", "task_closed");
        }

        if (task.HasChildren)
        {
            var children = setup.Tasks
                .Where(t => string.Equals(t.ParentTaskUid, task.Uid, StringComparison.Ordinal) && t.OpenForTime)
                .Take(MaxListedNames)
                .Select(t => $"{t.Path ?? t.Name} (WBS {t.WbsCode})")
                .ToList();
            throw new ProjectorApiException(
                $"Task '{task.Path ?? task.Name}' (WBS {task.WbsCode}) on project {projectCode} is a summary task with " +
                "sub-tasks; Projector rejects time on it when the time sheet is submitted. Nothing was saved. " +
                (children.Count > 0
                    ? $"Pick one of its sub-tasks: {string.Join("; ", children)}."
                    : "None of its sub-tasks is open for time: ask the project manager which task to use, or pick another task."),
                "summary_task");
        }

        var roles = await GetRolesAsync(connection, projectCode, date, ct);
        if (roles.Count == 0)
        {
            throw new ProjectorApiException(
                $"You have no role on project {projectCode} on {date}, so Projector won't accept time there. " +
                "Ask the project manager to add you, or pick a project with chargeable = true from list_time_projects.",
                "no_role_on_project");
        }

        var role = Resolve(roles, roleInput, r => r.Uid, r => r.Name, "role", projectCode);
        var (assignments, _) = await TryGetAssignmentsAsync(connection, setup, ct);
        if (assignments?.Restricted == true && !assignments.IsAssigned(task.Uid, [role.Uid]))
        {
            throw new ProjectorApiException(
                $"On project {projectCode} only people assigned to a task can submit time on it, and your role " +
                $"'{role.Name}' is not assigned to '{task.Path ?? task.Name}' (WBS {task.WbsCode}). Nothing was saved. " +
                "Pick a task with assigned = true in get_timecard_options, or ask the project manager to assign you.",
                "not_assigned_to_task");
        }

        var rateType = DefaultRateType(task, setup, projectCode);

        if (rules.RequireLocation && string.IsNullOrWhiteSpace(input.Location))
        {
            throw new ArgumentException("Projector requires a location on every time card; pass location.");
        }

        var request = new TimecardSaveRequest
        {
            TimecardUid = timecardUid,
            Timestamp = existing?.Timestamp,
            WorkDate = date,
            WorkMinutes = minutes,
            ProjectCode = projectCode,
            TaskUid = task.Uid,
            RoleUid = role.Uid,
            RateTypeUid = rateType.Uid,
            Description = narrative,
            LocationName = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim(),
            Udf1 = BuildUdf("udf1", input.Udf1, rules.Udf1, setup.Udf1Treatment),
            Udf2 = BuildUdf("udf2", input.Udf2, rules.Udf2, setup.Udf2Treatment)
        };
        return new PreparedCard(index, request, setup, task, role, rateType, existing);
    }

    /// <summary>A card's own problem (bad input, a Projector refusal, a failed lookup), not a cancelled call.</summary>
    private static bool IsCardError(Exception ex, CancellationToken ct) =>
        ex is ProjectorApiException or ArgumentException or HttpRequestException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private CardOutcome Failed(int index, string status, Exception ex)
    {
        var code = ex switch
        {
            ProjectorApiException api => api.ErrorCode ?? "projector_error",
            ArgumentException => "invalid_argument",
            _ => "projector_unavailable"
        };
        var message = ex is ProjectorApiException or ArgumentException
            ? ex.Message
            : "Projector could not be reached while checking this card; nothing was saved for it. Try again.";
        _logger.LogWarning("save_timecard card {Index} {Status}: {ErrorCode} {ErrorMessage}", index, status, code, message);
        return new CardOutcome(index, status, new { index, status, error = code, message }, ex, ErrorCode: code);
    }

    private static Timecard ToTimecard(PreparedCard card, string? uid, string statusCode) => new()
    {
        TimecardUid = uid,
        ProjectCode = card.Setup.ProjectCode,
        ProjectName = card.Setup.ProjectName,
        WorkDate = card.Request.WorkDate,
        WorkMinutes = card.Request.WorkMinutes,
        WorkHours = card.Request.WorkMinutes / 60.0,
        Status = StatusName(statusCode),
        CardStatusCode = statusCode,
        TaskName = card.Task.Name,
        TaskPath = card.Task.Path,
        TaskWbsCode = card.Task.WbsCode,
        ProjectTaskUid = card.Task.Uid,
        RoleName = card.Role.Name,
        ProjectRoleUid = card.Role.Uid,
        RateTypeName = card.RateType.Name,
        ProjectRateTypeUid = card.RateType.Uid,
        Description = card.Request.Description
    };

    /// <summary>Adds or replaces (same UID) a card in that date's list; a date whose cards couldn't be read stays unknown.</summary>
    private static void AddToDay(Dictionary<string, List<Timecard>?> days, string date, Timecard card)
    {
        if (days[date] is not { } list)
        {
            return;
        }

        if (card.TimecardUid is not null)
        {
            list.RemoveAll(c => string.Equals(c.TimecardUid, card.TimecardUid, StringComparison.Ordinal));
        }

        list.Add(card);
    }

    private static object? DayView(Dictionary<string, List<Timecard>?> days, string date) =>
        days.TryGetValue(date, out var list) && list is not null
            ? new { work_date = date, total_hours = list.Sum(c => c.WorkMinutes) / 60.0, card_count = list.Count }
            : null;

    private static object CardView(
        PreparedCard card,
        string status,
        string? uid,
        string statusCode,
        object? day,
        List<string> warnings,
        string? note) => new
        {
            index = card.Index,
            status,
            action = card.IsUpdate ? "updated" : "created",
            timecard = new
            {
                timecard_uid = uid,
                work_date = card.Request.WorkDate,
                hours = card.Request.WorkMinutes / 60.0,
                minutes = card.Request.WorkMinutes,
                project_code = card.Setup.ProjectCode,
                project_name = card.Setup.ProjectName,
                task = card.Task.Name,
                task_path = card.Task.Path,
                wbs_code = card.Task.WbsCode,
                role = card.Role.Name,
                rate_type = card.RateType.Name,
                narrative = card.Request.Description,
                status = StatusName(statusCode),
                status_code = statusCode
            },
            day,
            submitted = false,
            note,
            warnings = warnings.Count == 0 ? null : warnings
        };

    /// <summary>
    /// Finds a task by UID, full path ("Parent > Task"), WBS code or unique name. Names repeat under different
    /// parents, so an ambiguous name lists each candidate's path for the agent to choose from.
    /// </summary>
    internal static TimeEntryTask ResolveTask(IReadOnlyList<TimeEntryTask> tasks, string input, string projectCode)
    {
        var wanted = input.Trim();
        var byUid = tasks.FirstOrDefault(t => string.Equals(t.Uid, wanted, StringComparison.Ordinal));
        if (byUid is not null)
        {
            return byUid;
        }

        var wantedPath = NormalizePath(wanted);
        var byPath = tasks.Where(t => t.Path is not null && string.Equals(NormalizePath(t.Path), wantedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byPath.Count == 1)
        {
            return byPath[0];
        }

        // A path from list_timecards may start below the top level (card reads carry only the task and its parent's
        // name), so accept the tail of the full path when exactly one task ends that way.
        if (byPath.Count == 0 && wantedPath.Contains(TaskPaths.Separator, StringComparison.Ordinal))
        {
            var byTail = tasks.Where(t => t.Path is not null
                    && NormalizePath(t.Path).EndsWith(TaskPaths.Separator + wantedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byTail.Count == 1)
            {
                return byTail[0];
            }

            byPath = byTail;
        }

        var byWbs = tasks.Where(t => string.Equals(t.WbsCode?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byWbs.Count == 1)
        {
            return byWbs[0];
        }

        var byName = tasks.Where(t => string.Equals(t.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var openByName = byName.Where(t => t.AcceptsTime).ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }

        if (openByName.Count == 1)
        {
            return openByName[0];
        }

        var candidates = byName.Count > 0 ? byName : byPath;
        if (candidates.Count > 1)
        {
            throw new ProjectorApiException(
                $"{candidates.Count} tasks on project {projectCode} match '{wanted}': " +
                string.Join("; ", candidates.Take(MaxListedNames).Select(t => $"{t.Path ?? t.Name} (WBS {t.WbsCode}, {t.Uid})")) +
                ". Pass the task path, WBS code or UID instead.",
                "ambiguous_task");
        }

        throw new ProjectorApiException(
            $"Unknown task '{wanted}' on project {projectCode}. Pass the WBS code instead (taskWbsCode in list_timecards), " +
            $"or call get_timecard_options with project_code {projectCode} and query set to part of the task name, its WBS " +
            "code or its parent's name.",
            "invalid_task");
    }

    /// <summary>
    /// A same-project, same-task card with a similar narrative. Same hours alone is not enough: separate
    /// meetings on one task (two 30-minute 1:1s under "Staff Management") are normal.
    /// </summary>
    internal static Timecard? FindLikelyDuplicate(
        IEnumerable<Timecard> dayCards,
        string projectCode,
        string taskUid,
        int minutes,
        string narrative) =>
        dayCards.FirstOrDefault(c =>
            string.Equals(c.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ProjectTaskUid, taskUid, StringComparison.Ordinal)
            && SimilarText(c.Description, narrative));

    /// <summary>
    /// A card on the same day under a different task (or project) with a similar narrative: the same work entered
    /// twice under two tasks (seen 2026-09-28, J4). Checked only when there is no same-task duplicate.
    /// </summary>
    internal static Timecard? FindSimilarOnOtherTask(
        IEnumerable<Timecard> dayCards,
        string projectCode,
        string taskUid,
        string narrative) =>
        dayCards.FirstOrDefault(c =>
            !(string.Equals(c.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase)
              && string.Equals(c.ProjectTaskUid, taskUid, StringComparison.Ordinal))
            && SimilarText(c.Description, narrative));

    /// <summary>Fewest earlier cards on a project before a different role is worth a warning.</summary>
    internal const int MinCardsForRoleCheck = 2;

    /// <summary>
    /// The day's cards from before this call plus the recent cards list_time_projects already read (cache only; this
    /// adds no Projector call). Used for the role check.
    /// </summary>
    private IReadOnlyList<Timecard> PriorCards(ProjectorConnection connection, string date, IReadOnlyList<Timecard>? dayCards)
    {
        var cards = new List<Timecard>(dayCards ?? []);
        if (_cache.TryGet<IReadOnlyList<Timecard>>(connection, RecentKind, date, out var recent) && recent is not null)
        {
            cards.AddRange(recent.Where(r => r.TimecardUid is null
                || !cards.Any(c => string.Equals(c.TimecardUid, r.TimecardUid, StringComparison.Ordinal))));
        }

        return cards;
    }

    /// <summary>
    /// A warning when the card's role is not the one the user's earlier cards on that project use (at least
    /// <see cref="MinCardsForRoleCheck"/> of them, none with this role). Warn only: several roles on a project are
    /// legitimate, and Projector checks the role itself.
    /// </summary>
    internal static string? RoleWarning(
        string projectCode,
        string roleUid,
        string? roleName,
        IEnumerable<Timecard> earlierCards)
    {
        var onProject = earlierCards
            .Where(c => string.Equals(c.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.ProjectRoleUid))
            .ToList();
        if (onProject.Count < MinCardsForRoleCheck
            || onProject.Any(c => string.Equals(c.ProjectRoleUid, roleUid, StringComparison.Ordinal)))
        {
            return null;
        }

        var usual = onProject.GroupBy(c => c.ProjectRoleUid!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .First();
        return $"Role check: this card uses role '{roleName}', but your {onProject.Count} earlier card(s) on {projectCode} " +
            $"use '{usual.First().RoleName ?? usual.Key}'. The card is kept; confirm the role with the user.";
    }

    private static string? RoleWarning(PreparedCard card, IEnumerable<Timecard> earlierCards) =>
        RoleWarning(card.Setup.ProjectCode, card.Role.Uid, card.Role.Name, earlierCards);

    internal static bool SimilarText(string? a, string? b)
    {
        var wordsA = Words(a);
        var wordsB = Words(b);
        if (wordsA.Count == 0 || wordsB.Count == 0)
        {
            return false;
        }

        var shared = wordsA.Intersect(wordsB).Count();
        return shared / (double)Math.Min(wordsA.Count, wordsB.Count) >= 0.75;
    }

    private static HashSet<string> Words(string? text) =>
        (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 3)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizePath(string path) =>
        string.Join(TaskPaths.Separator, path.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string? text) =>
        text is null ? string.Empty : text.Length <= 80 ? text : text[..77] + "...";

    /// <summary>Items that match (score > 0), best match first, otherwise in their original order.</summary>
    private static List<T> Ranked<T>(IEnumerable<T> items, Func<T, int> score) =>
        items.Select((item, index) => (item, index, score: score(item)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    /// <summary>The set of rate types most open tasks allow; null when there are no tasks.</summary>
    private static IReadOnlyList<TimeEntryRateType>? CommonRateTypes(IReadOnlyList<TimeEntryTask> tasks, TimeEntryProjectSetup setup) =>
        tasks
            .Select(t => RateTypesFor(t, setup))
            .GroupBy(r => string.Join('|', r.Select(x => x.Uid)))
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();

    private static bool SameRateTypes(IReadOnlyList<TimeEntryRateType> a, IReadOnlyList<TimeEntryRateType>? b) =>
        b is not null && a.Select(r => r.Uid).SequenceEqual(b.Select(r => r.Uid));

    internal static string ParseWorkDate(string? workDate)
    {
        var raw = workDate?.Trim();
        if (string.IsNullOrEmpty(raw)
            || !DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException("work_date must be a date in yyyy-MM-dd format.");
        }

        return raw;
    }

    internal static int ToMinutes(double hours, int incrementMinutes)
    {
        var exact = hours * 60;
        var minutes = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        if (Math.Abs(exact - minutes) > 0.001)
        {
            throw new ArgumentException($"hours {hours.ToString(CultureInfo.InvariantCulture)} is not a whole number of minutes.");
        }

        if (incrementMinutes > 1 && minutes % incrementMinutes != 0)
        {
            var step = incrementMinutes / 60.0;
            var lower = Math.Floor(minutes / (double)incrementMinutes) * step;
            throw new ArgumentException(
                $"Projector records time in {incrementMinutes}-minute steps; hours must be a multiple of " +
                $"{step.ToString(CultureInfo.InvariantCulture)} (e.g. {lower.ToString(CultureInfo.InvariantCulture)} " +
                $"or {(lower + step).ToString(CultureInfo.InvariantCulture)}).");
        }

        return minutes;
    }

    internal static T Resolve<T>(
        IReadOnlyList<T> items,
        string input,
        Func<T, string> uid,
        Func<T, string?> name,
        string kind,
        string projectCode)
    {
        var wanted = input.Trim();
        var byUid = items.FirstOrDefault(i => string.Equals(uid(i), wanted, StringComparison.Ordinal));
        if (byUid is not null)
        {
            return byUid;
        }

        var byName = items.Where(i => string.Equals(name(i)?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }

        if (byName.Count > 1)
        {
            throw new ProjectorApiException(
                $"More than one {kind} on project {projectCode} is named '{wanted}': " +
                string.Join(", ", byName.Select(i => $"{name(i)} ({uid(i)})")) + ". Pass the UID instead.",
                $"ambiguous_{kind}");
        }

        var valid = items.Select(name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(MaxListedNames).ToList();
        throw new ProjectorApiException(
            $"Unknown {kind} '{wanted}' on project {projectCode}. " +
            (valid.Count == 0 ? "There are none to choose from." : $"Valid values: {string.Join("; ", valid)}.") +
            " See get_timecard_options.",
            $"invalid_{kind}");
    }

    /// <summary>
    /// The rate type a card on this task gets, never the agent's or user's choice (a changed rate type changes
    /// billing): the task's default; else the only allowed rate type; else the project's common default (every
    /// task type on the project defaults to the same one), for tasks without a task type. Otherwise nothing is saved.
    /// </summary>
    internal static TimeEntryRateType DefaultRateType(TimeEntryTask task, TimeEntryProjectSetup setup, string projectCode)
    {
        var allowed = RateTypesFor(task, setup);
        var byDefault = allowed.FirstOrDefault(r => string.Equals(r.Uid, task.DefaultRateTypeUid, StringComparison.Ordinal));
        if (byDefault is not null)
        {
            return byDefault;
        }

        if (allowed.Count == 1)
        {
            return allowed[0];
        }

        if (setup.TaskTypeDefaultRateTypeUids.Count == 1)
        {
            var common = allowed.FirstOrDefault(r => string.Equals(r.Uid, setup.TaskTypeDefaultRateTypeUids[0], StringComparison.Ordinal));
            if (common is not null)
            {
                return common;
            }
        }

        throw new ProjectorApiException(
            $"Task '{task.Path ?? task.Name}' on project {projectCode} has no default rate type in Projector" +
            (allowed.Count == 0 ? " and no rate types" : $" and {allowed.Count} possible ones") +
            ", so save_timecard can't pick one. Nothing was saved; enter this card in Projector.",
            "no_default_rate_type");
    }

    /// <summary>What save_timecard will use for this task, or null when it would refuse.</summary>
    private static TimeEntryRateType? TryDefaultRateType(TimeEntryTask task, TimeEntryProjectSetup setup)
    {
        try
        {
            return DefaultRateType(task, setup, setup.ProjectCode);
        }
        catch (ProjectorApiException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<TimeEntryRateType> RateTypesFor(TimeEntryTask task, TimeEntryProjectSetup setup) =>
        task.AllowedRateTypes.Count > 0 ? task.AllowedRateTypes : setup.RateTypes;

    internal static string StatusName(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "D" => "Draft",
        "R" => "Rejected",
        "S" => "Submitted",
        "A" => "Approved",
        "B" => "Billed",
        "I" => "Invoiced",
        _ => code ?? "unknown"
    };

    /// <summary>
    /// Turns Projector save errors into messages a user can act on. Keeps Projector's code, except for
    /// UpdatePermissionDenied, which gets its own code so the agent can tell the user exactly what to ask for.
    /// </summary>
    internal static ProjectorApiException MapSaveError(ProjectorApiException ex)
    {
        if (string.Equals(ex.ErrorCode, "UpdatePermissionDenied", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectorApiException(WebServicesAccessViewOnlyMessage, WebServicesAccessViewOnly, ex);
        }

        var message = ex.ErrorCode switch
        {
            "NoPermissionToSaveTimeCard" => "Projector says you don't have permission to save this time card.",
            "TimecardHasBeenChanged" =>
                "The card changed in Projector since it was read. Read it again with list_timecards and retry.",
            "CannotChangeSubmittedTimecardsApClosed" => "The accounting period for this date is closed in Projector.",
            "SpecifiedTimeCardDoesNotExist" => "The time card no longer exists in Projector.",
            "UnableToUpdateTimeCard" => "Projector could not update this time card: " + ex.Message,
            _ => null
        };

        return message is null ? ex : new ProjectorApiException(message, ex.ErrorCode, ex);
    }

    private static object? DescribeUdf(TimeEntryUdf? udf, string? treatment)
    {
        if (udf is null || !IsUdfUsed(treatment))
        {
            return null;
        }

        return new
        {
            name = udf.Name,
            required = udf.Required || IsUdfRequired(treatment),
            values = udf.Values.Count == 0 ? null : udf.Values
        };
    }

    private static TimecardUdfValue? BuildUdf(string param, string? value, TimeEntryUdf? udf, string? treatment)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (udf is null || !IsUdfUsed(treatment))
        {
            return text is null
                ? null
                : throw new ArgumentException($"{param} is not used on this project; leave it empty.");
        }

        if (text is null)
        {
            return udf.Required || IsUdfRequired(treatment)
                ? throw new ArgumentException($"Projector requires {param} ('{udf.Name}') on this project.")
                : null;
        }

        if (!string.Equals(udf.DataType, "T", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{param} ('{udf.Name}') is not a text field; set it in Projector.");
        }

        if (udf.Values.Count > 0 && !udf.Values.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{param} must be one of: {string.Join("; ", udf.Values.Take(MaxListedNames))}.");
        }

        return new TimecardUdfValue(udf.Uid, udf.Name, text);
    }

    private static bool IsUdfUsed(string? treatment) =>
        string.Equals(treatment, "R", StringComparison.OrdinalIgnoreCase)
        || string.Equals(treatment, "A", StringComparison.OrdinalIgnoreCase);

    private static bool IsUdfRequired(string? treatment) =>
        string.Equals(treatment, "R", StringComparison.OrdinalIgnoreCase);

    private Task<TimeEntryParameters> GetRulesAsync(ProjectorConnection connection, CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, RulesKind, string.Empty, TimeEntryCache.RulesTtl, () =>
            WithRefreshAsync(connection, c => _timeEntry.GetTimeEntryParametersAsync(c, ct), ct));

    /// <summary>All projects the user can enter time on for the date, with the user's roles (one call, cached).</summary>
    private Task<IReadOnlyList<TimeEntryProjectSummary>> GetProjectsAsync(
        ProjectorConnection connection,
        string date,
        CancellationToken ct) =>
        _cache.GetOrLoadAsync(connection, ProjectsKind, date, TimeEntryCache.LookupTtl, () =>
            WithRefreshAsync(connection, c => _timeEntry.SearchTimeEntryProjectsAsync(c, date, query: null, projectCode: null, ct), ct));

    private async Task<TimeEntryProjectSetup?> GetSetupAsync(
        ProjectorConnection connection,
        string projectCode,
        string date,
        CancellationToken ct)
    {
        var args = $"{projectCode.ToUpperInvariant()}|{date}";
        if (_cache.TryGet<TimeEntryProjectSetup>(connection, SetupKind, args, out var cached))
        {
            return cached;
        }

        var setup = await WithRefreshAsync(connection, c => _timeEntry.GetTimeEntryProjectAsync(c, projectCode, date, ct), ct);
        if (setup is not null)
        {
            _cache.Set(connection, SetupKind, args, setup, TimeEntryCache.LookupTtl);
        }

        return setup;
    }

    /// <summary>
    /// Task assignments, read only for projects whose AllowAssignmentFlag is false (the ones that restrict time entry to
    /// assigned roles). A failed read never blocks: the result is null with a note, and Projector still checks at submit.
    /// </summary>
    private async Task<(TaskAssignments? Assignments, string? Note)> TryGetAssignmentsAsync(
        ProjectorConnection connection,
        TimeEntryProjectSetup setup,
        CancellationToken ct)
    {
        if (setup.AllowAssignment)
        {
            return (null, null);
        }

        try
        {
            var assignments = await _cache.GetOrLoadAsync(
                connection, AssignmentsKind, setup.ProjectCode.ToUpperInvariant(), TimeEntryCache.LookupTtl, () =>
                    WithRefreshAsync(connection, c => _timeEntry.GetTaskAssignmentsAsync(c, setup.ProjectCode, ct), ct));
            return (assignments, assignments.Restricted
                ? "Only people assigned to a task can submit time on it here: tasks with assigned = false are rejected " +
                  "at submit unless the project manager assigns you. save_timecard refuses them."
                : null);
        }
        catch (Exception ex) when (ex is ProjectorApiException or HttpRequestException or TaskCanceledException
            && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read task assignments for {ProjectCode}", setup.ProjectCode);
            return (null, "This project may accept time only on tasks you are assigned to, and the assignments could " +
                "not be read. Prefer tasks you have posted to before; Projector checks it when the time sheet is submitted.");
        }
    }

    private async Task<IReadOnlyList<TimeEntryRole>> GetRolesAsync(
        ProjectorConnection connection,
        string projectCode,
        string date,
        CancellationToken ct)
    {
        var projects = await GetProjectsAsync(connection, date, ct);
        return projects.FirstOrDefault(p => string.Equals(p.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase))?.Roles
            ?? [];
    }

    private async Task<IReadOnlyList<Timecard>?> TryGetDayCardsAsync(
        ProjectorConnection connection,
        string date,
        CancellationToken ct)
    {
        try
        {
            return await _cache.GetOrLoadAsync(connection, DayCardsKind, date, TimeEntryCache.DayCardsTtl, () =>
                WithRefreshAsync(connection, c => _timeEntry.ListOwnTimecardsAsync(c, date, ct), ct));
        }
        catch (Exception ex) when (ex is ProjectorApiException or HttpRequestException or TaskCanceledException
            && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "save_timecard: could not read the day's cards for {WorkDate}", date);
            return null;
        }
    }

    private static ProjectorApiException ProjectNotFound(string projectCode, string date) =>
        new($"Project {projectCode} was not found for time entry on {date}. Use list_time_projects to find it.",
            "project_not_found");

    private static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value.Trim();

    private Task<ProjectorConnection> RequireAsync(string connectionId, CancellationToken ct) =>
        _connections.RequireConnectionAsync(connectionId, requiredScope: null, ct);

    // Same refresh-once rule as ProjectorToolService: an invalid session means Projector rejected the call
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
