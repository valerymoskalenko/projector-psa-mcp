using System.Globalization;
using Microsoft.Extensions.Logging;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Application.Resources;
using Projector.Domain.Auth;
using Projector.Domain.Bookings;
using Projector.Domain.Engagements;
using Projector.Domain.Exceptions;
using Projector.Domain.Resources;
using Projector.Domain.Timecards;

namespace Projector.Application.Tools;

public sealed record BookingExtraDayInput(string Date, double Hours, string? Comment = null);

public sealed record BookingCommentInput(string Date, string Text);

/// <summary>
/// save_booking: book one person on a project role for a date range (scheduler mode). Never requests, submits or
/// finalizes. Optional role create and task assignment when missing.
/// </summary>
public sealed class BookingToolService
{
    public const string WebServicesAccessViewOnly = TimeEntryToolService.WebServicesAccessViewOnly;

    private const string WriteOutcomeUnknown = ProjectorTimeEntryClient.WriteOutcomeUnknown;

    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorBookingClient _bookings;
    private readonly IProjectorSoapClient _soap;
    private readonly IProjectorResourceClient _resources;
    private readonly ILogger<BookingToolService> _logger;

    public BookingToolService(
        ProjectorConnectionService connections,
        IProjectorBookingClient bookings,
        IProjectorSoapClient soap,
        IProjectorResourceClient resources,
        ILogger<BookingToolService> logger)
    {
        _connections = connections;
        _bookings = bookings;
        _soap = soap;
        _resources = resources;
        _logger = logger;
    }

    public async Task<object> SaveBookingAsync(
        string connectionId,
        string resource,
        string projectCode,
        string startDate,
        string endDate,
        double hours,
        string schedulingMode,
        string? task,
        string? roleName,
        IReadOnlyList<BookingExtraDayInput>? extraDays,
        IReadOnlyList<BookingCommentInput>? comments,
        bool dryRun,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return Refused("resource is required (name, e-mail or id). A booking write never defaults to the signed-in user.");
        }

        if (string.IsNullOrWhiteSpace(projectCode))
        {
            return Refused("project_code is required.");
        }

        var connection = await RequireAsync(connectionId, ct);
        var person = await ResolvePersonAsync(connection, resource.Trim(), ct);
        var code = projectCode.Trim();

        var roles = await WithRefreshAsync(connection, c => _soap.ListProjectRolesAsync(c, [code], ct), ct);
        var plan = string.IsNullOrWhiteSpace(task)
            ? null
            : await WithRefreshAsync(connection, c => _soap.GetProjectTaskPlanAsync(c, code, ct), ct);

        ProjectPlanTask? matchedTask = null;
        if (!string.IsNullOrWhiteSpace(task))
        {
            matchedTask = FindTask(plan, task.Trim());
            if (matchedTask is null)
            {
                return Refused(
                    $"Task '{task.Trim()}' was not found on {code}. Use list_project_roles with include_task_plan.",
                    new { project_code = code, task });
            }

            if (matchedTask.OpenForTime == false)
            {
                return Refused($"Task '{TaskLabel(matchedTask)}' is closed for time.", new { project_code = code, task = TaskLabel(matchedTask) });
            }

            var isSummary = plan!.Tasks.Any(t =>
                string.Equals(t.ParentTaskUid, matchedTask.TaskUid, StringComparison.Ordinal));
            if (isSummary)
            {
                return Refused(
                    $"Task '{TaskLabel(matchedTask)}' is a summary task (it has child tasks). Book a leaf task.",
                    new { project_code = code, task = TaskLabel(matchedTask) });
            }
        }

        var personRoles = roles.Roles
            .Where(r => MatchesPerson(r, person))
            .ToList();

        var (role, roleError, willCreateRole) = PickRole(
            personRoles, matchedTask, roleName, person.DisplayName, roles.Roles);
        if (roleError is not null)
        {
            return Refused(roleError, new
            {
                project_code = code,
                resource = person.DisplayName,
                roles = personRoles.Select(r => new { r.RoleUid, r.RoleName, r.DisplayName }).ToList()
            });
        }

        var willAssignTask = matchedTask is not null
            && role?.RoleUid is not null
            && matchedTask.Roles.All(r => !string.Equals(r.RoleUid, role.RoleUid, StringComparison.Ordinal))
            && !willCreateRole;

        // When creating a role, assign to the task after create.
        if (willCreateRole && matchedTask is not null)
        {
            willAssignTask = true;
        }

        var weekCount = Math.Max(1, ProjectorDateHelpers.GetMinimumWeekCountForWindow(startDate, endDate) + 1);
        IReadOnlyList<RoleScheduleState> schedules = [];
        if (!willCreateRole && role?.RoleUid is not null)
        {
            schedules = await WithRefreshAsync(
                connection,
                c => _bookings.GetRoleSchedulesAsync(c, code, startDate, weekCount, ct),
                ct);
        }

        var currentRole = schedules.FirstOrDefault(s =>
            string.Equals(s.RoleUid, role?.RoleUid, StringComparison.Ordinal));
        var currentByWeek = (currentRole?.Weeks ?? [])
            .ToDictionary(w => w.WeekStart!, w => w, StringComparer.Ordinal);

        var planResult = BookingPlanner.Plan(
            startDate,
            endDate,
            schedulingMode,
            hours,
            (extraDays ?? []).Select(e => new BookingPlanner.ExtraDay(e.Date, e.Hours, e.Comment)).ToList(),
            (comments ?? []).Select(c => new BookingPlanner.CommentDay(c.Date, c.Text)).ToList(),
            currentByWeek);

        if (planResult.Errors.Count > 0)
        {
            return Refused(string.Join(" ", planResult.Errors), new
            {
                project_code = code,
                start_date = startDate,
                end_date = endDate
            });
        }

        var weeksView = planResult.Weeks.Select(w => new
        {
            week_start = w.WeekStart,
            scheduling_mode = w.SchedulingMode == "D" ? "daily" : "weekly",
            previous_hours = RoundHours(w.PreviousMinutes),
            new_hours = RoundHours(w.NewMinutes),
            weekly_hours = w.WeeklyMinutes is null ? (double?)null : RoundHours(w.WeeklyMinutes.Value),
            daily_hours = w.DailyMinutes?.Select(RoundHours).ToList(),
            notes = w.Notes is null
                ? null
                : Enumerable.Range(0, 7)
                    .Where(i => !string.IsNullOrEmpty(w.Notes[i]))
                    .Select(i => new
                    {
                        day = ((DayOfWeek)i).ToString(),
                        date = DateTime.ParseExact(w.WeekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                            .AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        text = w.Notes[i]
                    }).ToList()
        }).ToList();

        var preview = new Dictionary<string, object?>
        {
            ["action"] = dryRun ? "dry_run" : "saved",
            ["project_code"] = code,
            ["resource"] = new
            {
                display_name = person.DisplayName,
                email = person.Email,
                resource_id = person.ReferenceSystemId,
                resource_uid = person.ResourceUid
            },
            ["role"] = new
            {
                role_uid = role?.RoleUid,
                role_name = role?.RoleName ?? person.DisplayName,
                create = willCreateRole
            },
            ["task"] = matchedTask is null
                ? null
                : new
                {
                    task_uid = matchedTask.TaskUid,
                    task_name = matchedTask.TaskName,
                    wbs_code = matchedTask.WbsCode,
                    task_path = TaskLabel(matchedTask),
                    assign = willAssignTask
                },
            ["start_date"] = startDate,
            ["end_date"] = endDate,
            ["scheduling_mode"] = schedulingMode.Trim().ToLowerInvariant(),
            ["hours_per_period"] = hours,
            ["weeks"] = weeksView,
            ["skipped_weeks"] = planResult.SkippedWeeks,
            ["note"] =
                "Hours are booked on the role (Resource Scheduling grid), not on the task plan. " +
                "Task-plan effort is not changed. Show this dry run and save only after the user confirms."
        };

        if (dryRun)
        {
            return preview;
        }

        string? createdRoleUid = null;
        var assignedTask = false;
        try
        {
            if (willCreateRole)
            {
                var created = await WithRefreshAsync(
                    connection,
                    c => _bookings.SaveProjectRoleAsync(c, new SaveProjectRoleRequest
                    {
                        ProjectCode = code,
                        RoleName = (person.DisplayName ?? "Role").Trim(),
                        ResourceReferenceSystemId = person.ReferenceSystemId,
                        ResourceUid = person.ResourceUid,
                        DefaultSchedulingMode = "W"
                    }, ct),
                    ct);
                createdRoleUid = created.RoleUid;
                role = new ProjectRoleAssignment
                {
                    ProjectCode = code,
                    RoleUid = created.RoleUid,
                    RoleName = person.DisplayName,
                    ResourceId = person.ReferenceSystemId,
                    DisplayName = person.DisplayName,
                    Email = person.Email
                };
                preview["role"] = new
                {
                    role_uid = role.RoleUid,
                    role_name = role.RoleName,
                    create = true,
                    created = true
                };
            }

            if (willAssignTask && role?.RoleUid is not null && matchedTask?.TaskUid is not null)
            {
                await WithRefreshAsync(connection, async c =>
                {
                    await _bookings.SaveProjectTaskRoleAsync(c, new SaveProjectTaskRoleRequest
                    {
                        RoleUid = role.RoleUid,
                        TaskUid = matchedTask.TaskUid,
                        EffortMinutes = 0
                    }, ct);
                    return true;
                }, ct);
                assignedTask = true;
            }

            var hoursBuckets = planResult.Weeks.Select(w => new RoleHoursBucket
            {
                WeekStart = w.WeekStart,
                SchedulingMode = w.SchedulingMode,
                WeeklyMinutes = w.WeeklyMinutes,
                DailyMinutes = w.DailyMinutes,
                Notes = null
            }).ToList();

            var notesBuckets = planResult.Weeks
                .Where(w => w.NotesChanged && w.Notes is not null)
                .Select(w => new RoleHoursBucket
                {
                    WeekStart = w.WeekStart,
                    SchedulingMode = w.SchedulingMode,
                    Notes = w.Notes
                }).ToList();

            await WithRefreshAsync(connection, async c =>
            {
                await _bookings.BookRoleHoursAsync(c, new BookRoleHoursRequest
                {
                    RoleUid = role!.RoleUid!,
                    HoursBuckets = hoursBuckets,
                    NotesBuckets = notesBuckets
                }, ct);
                return true;
            }, ct);
        }
        catch (ProjectorApiException ex)
        {
            throw MapSaveError(ex, createdRoleUid, assignedTask);
        }

        // Read back.
        var after = await WithRefreshAsync(
            connection,
            c => _bookings.GetRoleSchedulesAsync(c, code, startDate, weekCount, ct),
            ct);
        var afterRole = after.FirstOrDefault(s => string.Equals(s.RoleUid, role!.RoleUid, StringComparison.Ordinal));
        var readBack = planResult.Weeks.Select(w =>
        {
            var week = afterRole?.Weeks.FirstOrDefault(x => x.WeekStart == w.WeekStart);
            var actual = week?.WeeklyMinutes ?? week?.DailyMinutes.Sum() ?? 0;
            return new
            {
                week_start = w.WeekStart,
                expected_hours = RoundHours(w.NewMinutes),
                actual_hours = RoundHours(actual),
                matches = actual == w.NewMinutes
            };
        }).ToList();

        preview["action"] = "saved";
        preview["read_back"] = readBack;
        preview["submitted"] = false;
        preview["finalized"] = false;
        if (readBack.Any(r => !r.matches))
        {
            preview["warning"] =
                "Some weeks did not match the expected hours after save. Check list_proj_bookings.";
        }

        return preview;
    }

    private async Task<BookingPerson> ResolvePersonAsync(
        ProjectorConnection connection, string input, CancellationToken ct)
    {
        // Prefer get_resource path (name / e-mail / uid) so people without an employee id still resolve.
        if (ResourceEmailResolver.LooksLikeEmail(input))
        {
            var match = await WithRefreshAsync(connection, c =>
                ResourceEmailResolver.FindAsync(_resources, c, input, ct), ct);
            if (match?.ResourceReferenceSystemId is null && match?.ResourceUid is null)
            {
                throw new ProjectorApiException($"No resource has the email '{input}'.", "AtLeastOneItemNotFound");
            }

            var byEmail = await WithRefreshAsync(connection, c =>
                _resources.GetResourceAsync(
                    c,
                    match.ResourceReferenceSystemId ?? match.ResourceUid!,
                    includeHistory: false,
                    includeUdfs: false,
                    ct), ct);
            return FromDetail(byEmail, match.ResourceReferenceSystemId, match.ResourceUid, match.DisplayName, match.EmailAddress);
        }

        try
        {
            var detail = await WithRefreshAsync(connection, c =>
                _resources.GetResourceAsync(c, input, includeHistory: false, includeUdfs: false, ct), ct);
            if (detail is not null)
            {
                return FromDetail(detail, detail.ResourceReferenceSystemId, detail.ResourceUid, detail.DisplayName, detail.EmailAddress);
            }
        }
        catch (ProjectorApiException)
        {
            // Fall through to list search.
        }

        var list = await WithRefreshAsync(connection, c =>
            _resources.ListResourcesAsync(c, input, includeInactive: false, maxRows: 50, ct), ct);
        var exact = list.Resources
            .Where(r => string.Equals(r.DisplayName?.Trim(), input, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = exact.Count > 0 ? exact : list.Resources
            .Where(r => r.DisplayName is not null && r.DisplayName.Contains(input, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new ProjectorApiException($"Resource not found for '{input}'.", "AtLeastOneItemNotFound");
        }

        if (candidates.Count > 1)
        {
            throw new ProjectorApiException(
                $"Several people match '{input}': {string.Join(", ", candidates.Take(5).Select(r => r.DisplayName))}. " +
                "Pass the full name or e-mail.",
                "AmbiguousResource");
        }

        var summary = candidates[0];
        var loaded = string.IsNullOrWhiteSpace(summary.ResourceReferenceSystemId)
            && string.IsNullOrWhiteSpace(summary.ResourceUid)
            ? null
            : await WithRefreshAsync(connection, c =>
                _resources.GetResourceAsync(
                    c,
                    summary.ResourceReferenceSystemId ?? summary.ResourceUid!,
                    includeHistory: false,
                    includeUdfs: false,
                    ct), ct);
        return FromDetail(
            loaded,
            summary.ResourceReferenceSystemId ?? loaded?.ResourceReferenceSystemId,
            summary.ResourceUid ?? loaded?.ResourceUid,
            summary.DisplayName ?? loaded?.DisplayName,
            summary.EmailAddress ?? loaded?.EmailAddress);
    }

    private static BookingPerson FromDetail(
        ResourceDetail? detail,
        string? referenceSystemId,
        string? resourceUid,
        string? displayName,
        string? email)
    {
        var uid = resourceUid ?? detail?.ResourceUid;
        var refId = referenceSystemId ?? detail?.ResourceReferenceSystemId;
        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(refId))
        {
            throw new ProjectorApiException("The resource has no ResourceUid or employee id.", "InvalidResourceId");
        }

        return new BookingPerson(
            refId,
            uid,
            displayName ?? detail?.DisplayName,
            email ?? detail?.EmailAddress);
    }

    private static (ProjectRoleAssignment? Role, string? Error, bool Create) PickRole(
        IReadOnlyList<ProjectRoleAssignment> personRoles,
        ProjectPlanTask? task,
        string? roleName,
        string? displayName,
        IReadOnlyList<ProjectRoleAssignment> allRoles)
    {
        if (!string.IsNullOrWhiteSpace(roleName))
        {
            var named = personRoles
                .Where(r => string.Equals(r.RoleName?.Trim(), roleName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (named.Count == 1)
            {
                return (named[0], null, false);
            }

            if (named.Count == 0)
            {
                return (null, $"No role named '{roleName.Trim()}' for this person on the project.", false);
            }

            return (null, $"Several roles are named '{roleName.Trim()}'.", false);
        }

        if (task is not null)
        {
            var onTask = personRoles
                .Where(r => task.Roles.Any(tr => string.Equals(tr.RoleUid, r.RoleUid, StringComparison.Ordinal)))
                .ToList();
            if (onTask.Count == 1)
            {
                return (onTask[0], null, false);
            }

            if (onTask.Count > 1)
            {
                return (null, "This person has several roles on the task. Pass role_name.", false);
            }
        }

        if (personRoles.Count == 1)
        {
            return (personRoles[0], null, false);
        }

        if (personRoles.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return (null, "This person is not on the project and has no display name to create a role.", false);
            }

            if (allRoles.Any(r => string.Equals(r.RoleName?.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return (null,
                    $"A role named '{displayName.Trim()}' already exists on the project (role_name_taken). " +
                    "Pass role_name of an existing role for this person, or rename in Projector.",
                    false);
            }

            return (null, null, true);
        }

        return (null, "This person has several roles on the project. Pass role_name.", false);
    }

    private static bool MatchesPerson(ProjectRoleAssignment role, BookingPerson person)
    {
        if (!string.IsNullOrWhiteSpace(person.ReferenceSystemId)
            && string.Equals(role.ResourceId, person.ReferenceSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(person.Email)
            && string.Equals(role.Email, person.Email, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(person.DisplayName)
            && string.Equals(role.DisplayName, person.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectPlanTask? FindTask(ProjectTaskPlan? plan, string query)
    {
        if (plan is null || plan.Tasks.Count == 0)
        {
            return null;
        }

        var paths = TaskPaths.Build(plan.Tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.TaskUid))
            .Select(t => (t.TaskUid!, t.TaskName, t.ParentTaskUid)));
        var exactWbs = plan.Tasks.Where(t => string.Equals(t.WbsCode, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactWbs.Count == 1)
        {
            return exactWbs[0];
        }

        var exactPath = plan.Tasks.Where(t =>
            paths.TryGetValue(t.TaskUid ?? "", out var path)
            && string.Equals(path, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactPath.Count == 1)
        {
            return exactPath[0];
        }

        var exactName = plan.Tasks.Where(t => string.Equals(t.TaskName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactName.Count == 1)
        {
            return exactName[0];
        }

        var contains = plan.Tasks.Where(t =>
            (t.TaskName is not null && t.TaskName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (paths.TryGetValue(t.TaskUid ?? "", out var path) && path.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    private static string TaskLabel(ProjectPlanTask task) =>
        string.IsNullOrWhiteSpace(task.WbsCode) ? task.TaskName ?? task.TaskUid ?? "task" : $"{task.WbsCode} {task.TaskName}";

    private static object Refused(string error, object? details = null) =>
        new Dictionary<string, object?>
        {
            ["action"] = "refused",
            ["error"] = error,
            ["details"] = details
        };

    private static double RoundHours(int minutes) => Math.Round(minutes / 60.0, 2);

    private static ProjectorApiException MapSaveError(
        ProjectorApiException ex, string? createdRoleUid, bool assignedTask)
    {
        if (string.Equals(ex.ErrorCode, "UpdatePermissionDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ex.ErrorCode, "NoPermissionToBookHours", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = createdRoleUid is null
                ? "Nothing was saved. "
                : $"A role was created (role_uid {createdRoleUid})"
                  + (assignedTask ? " and assigned to the task" : string.Empty)
                  + ", but hours were not booked. ";
            return new ProjectorApiException(
                prefix +
                "Your Projector user needs Web Services Access U (Update) and permission to book hours. " +
                "Ask your Projector PSA administrator, then try again. Do not retry until the permission is changed.",
                string.Equals(ex.ErrorCode, "NoPermissionToBookHours", StringComparison.OrdinalIgnoreCase)
                    ? "no_permission_to_book_hours"
                    : WebServicesAccessViewOnly,
                ex);
        }

        if (string.Equals(ex.ErrorCode, WriteOutcomeUnknown, StringComparison.OrdinalIgnoreCase))
        {
            var note = createdRoleUid is null
                ? string.Empty
                : $" A role may already exist (role_uid {createdRoleUid}).";
            return new ProjectorApiException(ex.Message + note, WriteOutcomeUnknown, ex);
        }

        return ex;
    }

    private Task<ProjectorConnection> RequireAsync(string connectionId, CancellationToken ct) =>
        _connections.RequireConnectionAsync(connectionId, requiredScope: null, ct);

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
            connection = await _connections.RefreshConnectionAsync(connection, ct);
            return await action(connection);
        }
    }

    private static bool IsAuthFailure(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    private sealed record BookingPerson(
        string? ReferenceSystemId,
        string? ResourceUid,
        string? DisplayName,
        string? Email);
}
