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
    public const int DefaultMaxTasks = 50;
    public const int MaxTasksLimit = 200;
    private const int MaxListedNames = 25;
    private const string RulesKind = "rules";
    private const string ProjectsKind = "projects";
    private const string SetupKind = "setup";
    private const string DayCardsKind = "day_cards";
    private const string AssignmentsKind = "assignments";
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
        int offset = 0)
    {
        var date = ParseWorkDate(workDate);
        maxRows = Math.Clamp(maxRows, 1, 200);
        offset = Math.Max(0, offset);
        var connection = await RequireAsync(connectionId, ct);
        var all = await GetProjectsAsync(connection, date, ct);

        // Filtered here, on the cached full list (Projector ignores MaximumRows anyway), so a new query or page
        // does not call Projector again.
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var matches = text is null
            ? all
            : Ranked(all, p => TextMatch.Score(text, p.ProjectCode, p.ProjectName, p.EngagementCode, p.EngagementName, p.ClientName));
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
                unavailable_reason = p.UnavailableReasonCode
            }).ToList(),
            next_step = text is not null && matches.Count == 0
                ? $"No project you can enter time on matches '{text}'. Projector lists only projects where you have a role: " +
                  "check the name with list_engagements, and ask the project manager to add you if it exists. The words may " +
                  "also be a task inside another project (e.g. a presale opportunity): try get_timecard_options with query."
                : "Call get_timecard_options with a project_code where chargeable is true and the same work_date " +
                  "(add query to find a task by name, WBS or parent)."
        };
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
            next_step = hasMore
                ? "More tasks match: narrow with query (task name, WBS or parent) or page with offset = tasks_next_offset."
                : "Confirm the card with the user (date, hours, project, task path, role, narrative), then call save_timecard."
        };
    }

    public async Task<object> SaveTimecardAsync(
        string connectionId,
        SaveTimecardInput input,
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
        var isUpdate = timecardUid is not null;
        var connection = await RequireAsync(connectionId, ct);

        var rules = await GetRulesAsync(connection, ct);
        var minutes = ToMinutes(input.Hours, rules.ReportingTimeIncrementMinutes);

        OwnTimecard? existing = null;
        if (isUpdate)
        {
            // Never cached: the Timestamp must be current or Projector rejects the update.
            existing = await WithRefreshAsync(connection, c => _timeEntry.GetOwnTimecardAsync(c, timecardUid!, date, ct), ct)
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

        // The day's cards feed the duplicate check and the day total. The save doesn't need them,
        // so a failed read only becomes a warning.
        var warnings = new List<string>();
        var dayCards = await TryGetDayCardsAsync(connection, date, ct);
        if (dayCards is null)
        {
            warnings.Add("Could not read your other cards for this date, so there is no duplicate check or day total. " +
                "Check with list_timecards.");
        }
        else if (!isUpdate)
        {
            var duplicate = FindLikelyDuplicate(dayCards, projectCode, task.Uid, minutes, narrative);
            if (duplicate is not null)
            {
                warnings.Add(
                    $"Possible duplicate: card {duplicate.TimecardUid} ({duplicate.WorkHours:0.##} h, {duplicate.Status}) " +
                    $"was already on {date} for the same project and task: \"{Shorten(duplicate.Description)}\". " +
                    "Both cards are kept; ask the user whether this new one should stay.");
            }
        }

        TimecardSaveResult saved;
        try
        {
            saved = await WithRefreshAsync(connection, c => _timeEntry.SaveTimecardAsync(c, request, ct), ct);
        }
        catch (ProjectorApiException ex)
        {
            _logger.LogWarning(
                "save_timecard failed: {ErrorCode} {ErrorMessage} (update={IsUpdate})",
                ex.ErrorCode,
                ex.Message,
                isUpdate);
            _cache.Remove(connection, DayCardsKind, date);
            throw MapSaveError(ex);
        }

        // Projector saves every create and every update as Draft: a Rejected card goes back to Draft when saved
        // (seen live 2026-09-25), and the user resubmits it like any other draft.
        const string expectedStatus = "D";
        var wasRejected = isUpdate && string.Equals(existing!.CardStatusCode, "R", StringComparison.OrdinalIgnoreCase);
        if (saved.SubmittedFlag)
        {
            _logger.LogError("save_timecard: Projector reported SubmittedFlag=true for card {TimecardUid}", saved.TimecardUid);
            warnings.Add("Projector reported the card as submitted, which save_timecard never requests. Check it in Projector.");
        }

        if (!string.IsNullOrWhiteSpace(saved.CardStatusCode)
            && !string.Equals(saved.CardStatusCode, expectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "save_timecard: card {TimecardUid} status {Actual}, expected {Expected}",
                saved.TimecardUid,
                saved.CardStatusCode,
                expectedStatus);
            warnings.Add($"The card is now {StatusName(saved.CardStatusCode)} (expected {StatusName(expectedStatus)}).");
        }

        var statusCode = saved.CardStatusCode ?? expectedStatus;
        var savedUid = saved.TimecardUid ?? timecardUid;
        object? day = null;
        if (dayCards is not null && savedUid is not null)
        {
            var updatedDay = dayCards
                .Where(c => !string.Equals(c.TimecardUid, savedUid, StringComparison.Ordinal))
                .Append(new Timecard
                {
                    TimecardUid = savedUid,
                    ProjectCode = setup.ProjectCode,
                    ProjectName = setup.ProjectName,
                    WorkDate = date,
                    WorkMinutes = minutes,
                    WorkHours = minutes / 60.0,
                    Status = StatusName(statusCode),
                    CardStatusCode = statusCode,
                    TaskName = task.Name,
                    TaskPath = task.Path,
                    TaskWbsCode = task.WbsCode,
                    ProjectTaskUid = task.Uid,
                    RoleName = role.Name,
                    ProjectRoleUid = role.Uid,
                    RateTypeName = rateType.Name,
                    ProjectRateTypeUid = rateType.Uid,
                    Description = narrative
                })
                .ToList();
            _cache.Set(connection, DayCardsKind, date, (IReadOnlyList<Timecard>)updatedDay, TimeEntryCache.DayCardsTtl);
            day = new
            {
                work_date = date,
                total_hours = updatedDay.Sum(c => c.WorkMinutes) / 60.0,
                card_count = updatedDay.Count
            };
        }
        else
        {
            _cache.Remove(connection, DayCardsKind, date);
        }

        return new
        {
            action = isUpdate ? "updated" : "created",
            timecard = new
            {
                timecard_uid = savedUid,
                work_date = date,
                hours = minutes / 60.0,
                minutes,
                project_code = setup.ProjectCode,
                project_name = setup.ProjectName,
                task = task.Name,
                task_path = task.Path,
                wbs_code = task.WbsCode,
                role = role.Name,
                rate_type = rateType.Name,
                narrative,
                status = StatusName(statusCode),
                status_code = statusCode
            },
            day,
            submitted = false,
            note = wasRejected
                ? "Saved. The card was Rejected and is now a Draft again, not submitted. Resubmit it in Projector."
                : "Saved as Draft, not submitted. Submit your time sheet in Projector when it is complete.",
            warnings = warnings.Count == 0 ? null : warnings
        };
    }

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
