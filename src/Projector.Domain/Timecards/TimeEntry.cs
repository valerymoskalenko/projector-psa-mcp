namespace Projector.Domain.Timecards;

/// <summary>A project the signed-in user can enter time on (PwsSearchProjects, ListType=T).</summary>
public sealed class TimeEntryProjectSummary
{
    public required string ProjectCode { get; init; }

    public string? ProjectUid { get; init; }

    public string? ProjectName { get; init; }

    public string? EngagementCode { get; init; }

    public string? EngagementName { get; init; }

    public string? ClientName { get; init; }

    public bool? Billable { get; init; }

    public string? LocationName { get; init; }

    public string? UnavailableReasonCode { get; init; }

    public IReadOnlyList<TimeEntryRole> Roles { get; init; } = [];
}

/// <summary>A project role the user can book time against.</summary>
public sealed record TimeEntryRole(string Uid, string? Name, string? StartDate, string? EndDate);

public sealed record TimeEntryRateType(string Uid, string? Name);

public sealed class TimeEntryTask
{
    public required string Uid { get; init; }

    public string? Name { get; init; }

    public string? WbsCode { get; init; }

    public string? ParentTaskName { get; init; }

    public string? ParentTaskUid { get; init; }

    /// <summary>Parent names and this task's name joined with " > " (see <see cref="TaskPaths"/>).</summary>
    public string? Path { get; set; }

    public bool OpenForTime { get; init; }

    /// <summary>
    /// True for a summary task (another task names it as parent). Projector still reports OpenForTimeFlag=true
    /// for these, but rejects time on them at submit ("time cannot be entered for this task").
    /// </summary>
    public bool HasChildren { get; set; }

    /// <summary>Open for time and not a summary task.</summary>
    public bool AcceptsTime => OpenForTime && !HasChildren;

    public string? TaskTypeName { get; init; }

    public bool NarrativeRequired { get; init; }

    /// <summary>Rate types allowed by the task's task type; empty when the task has no type.</summary>
    public IReadOnlyList<TimeEntryRateType> AllowedRateTypes { get; init; } = [];

    public string? DefaultRateTypeUid { get; init; }
}

/// <summary>One project's time-entry setup (PwsGetTimeEntryProjectRole).</summary>
public sealed class TimeEntryProjectSetup
{
    public required string ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? EngagementCode { get; init; }

    public string? ClientName { get; init; }

    public bool? Billable { get; init; }

    public bool OpenForTime { get; init; }

    public bool DescriptionRequired { get; init; }

    /// <summary>
    /// Project AllowAssignmentFlag. False on projects where only roles assigned to a task may post time to it
    /// (PwsGetProject TimeEntryRestrictedToRolesAssignedToTasksFlag); then see <see cref="TaskAssignments"/>.
    /// </summary>
    public bool AllowAssignment { get; init; } = true;

    /// <summary>R = required, A = allowed, anything else = not used.</summary>
    public string? Udf1Treatment { get; init; }

    public string? Udf2Treatment { get; init; }

    public IReadOnlyList<TimeEntryRateType> RateTypes { get; init; } = [];

    public IReadOnlyList<TimeEntryTask> Tasks { get; init; } = [];

    /// <summary>
    /// The default rate type UIDs of the project's task types (distinct). When they all agree, that is the
    /// project's common default, used for tasks that have no task type.
    /// </summary>
    public IReadOnlyList<string> TaskTypeDefaultRateTypeUids { get; init; } = [];
}

/// <summary>
/// Which roles are assigned to which tasks on one project (PwsGetProject). When <paramref name="Restricted"/> is true,
/// Projector rejects time at submit from a role that is not assigned to the task.
/// </summary>
public sealed record TaskAssignments(bool Restricted, IReadOnlyDictionary<string, IReadOnlySet<string>> RolesByTask)
{
    public bool IsAssigned(string taskUid, IEnumerable<string> roleUids) =>
        RolesByTask.TryGetValue(taskUid, out var roles) && roleUids.Any(roles.Contains);
}

public sealed record TimeEntryUdf(string? Uid, string? Name, string? DataType, bool Required, IReadOnlyList<string> Values);

/// <summary>Account-wide time-entry rules (PwsGetTimeEntryParameters).</summary>
public sealed class TimeEntryParameters
{
    public int ReportingTimeIncrementMinutes { get; init; } = 1;

    public bool RequireLocation { get; init; }

    public bool Enforce24HourDailyLimit { get; init; }

    public TimeEntryUdf? Udf1 { get; init; }

    public TimeEntryUdf? Udf2 { get; init; }
}

/// <summary>One of the signed-in user's own work cards, read back before an update.</summary>
public sealed class OwnTimecard
{
    public required string TimecardUid { get; init; }

    public string? WorkDate { get; init; }

    public int WorkMinutes { get; init; }

    public string? CardStatusCode { get; init; }

    public string? ProjectCode { get; init; }

    public string? TaskUid { get; init; }

    public string? RoleUid { get; init; }

    public string? RateTypeUid { get; init; }

    public string? Description { get; init; }

    /// <summary>Base64 row version; sent back on update so Projector rejects concurrent changes.</summary>
    public string? Timestamp { get; init; }
}

public sealed record TimecardUdfValue(string? UdfUid, string? UdfName, string TextValue);

/// <summary>
/// A work card to save for the signed-in user. Never carries a resource or a submit option:
/// the save always targets the caller's own time sheet and leaves the card unsubmitted.
/// </summary>
public sealed class TimecardSaveRequest
{
    /// <summary>Null = create a Draft card; set = update that card.</summary>
    public string? TimecardUid { get; init; }

    public string? Timestamp { get; init; }

    public required string WorkDate { get; init; }

    public required int WorkMinutes { get; init; }

    public required string ProjectCode { get; init; }

    public required string TaskUid { get; init; }

    public required string RoleUid { get; init; }

    public required string RateTypeUid { get; init; }

    public required string Description { get; init; }

    public string? LocationName { get; init; }

    public TimecardUdfValue? Udf1 { get; init; }

    public TimecardUdfValue? Udf2 { get; init; }
}

public sealed class TimecardSaveResult
{
    public string? TimecardUid { get; init; }

    public string? WorkDate { get; init; }

    public int? WorkMinutes { get; init; }

    public string? CardStatusCode { get; init; }

    public bool SubmittedFlag { get; init; }
}
