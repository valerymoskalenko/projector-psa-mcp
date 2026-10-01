namespace Projector.Domain.Engagements;

/// <summary>
/// A project's task plan (the Task Planning tab) from PwsGetProject with sub-entities. Rates in the same response
/// are deliberately not carried.
/// </summary>
public sealed class ProjectTaskPlan
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? PlanStartDate { get; init; }

    public string? PlanEndDate { get; init; }

    /// <summary>Length of the project's working day; turns a task's duration minutes into days.</summary>
    public int MinutesPerDay { get; init; } = 480;

    public IReadOnlyList<ProjectPlanTask> Tasks { get; init; } = [];
}

public sealed class ProjectPlanTask
{
    public string? TaskUid { get; init; }

    public string? ParentTaskUid { get; init; }

    public string? WbsCode { get; init; }

    public string? TaskName { get; init; }

    public string? TaskTypeName { get; init; }

    public int? DurationMinutes { get; init; }

    public string? EarliestStartDate { get; init; }

    public string? PlannedStartDate { get; init; }

    public string? PlannedEndDate { get; init; }

    public bool? OpenForTime { get; init; }

    public bool Completed { get; init; }

    public IReadOnlyList<string> PredecessorTaskUids { get; init; } = [];

    public IReadOnlyList<ProjectPlanTaskRole> Roles { get; init; } = [];
}

/// <summary>A role assigned to a task with its planned effort.</summary>
public sealed class ProjectPlanTaskRole
{
    public string? RoleUid { get; init; }

    public string? RoleName { get; init; }

    public int EffortMinutes { get; init; }

    public bool Completed { get; init; }
}
