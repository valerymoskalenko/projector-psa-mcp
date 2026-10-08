namespace Projector.Domain.Bookings;

/// <summary>One week of booked hours and notes for a project role (scheduler mode).</summary>
public sealed class RoleHoursBucket
{
    public required string WeekStart { get; init; }

    /// <summary>D = daily (seven minute values), W = weekly total.</summary>
    public required string SchedulingMode { get; init; }

    public int? WeeklyMinutes { get; init; }

    /// <summary>Sunday first when SchedulingMode is D. Null for weekly.</summary>
    public IReadOnlyList<int>? DailyMinutes { get; init; }

    /// <summary>Seven note strings, Sunday first. Null when notes are not being written.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>Current booked week on a role, from PwsGetResourceSchedulingRoleData.</summary>
public sealed class RoleWeekState
{
    public required string WeekStart { get; init; }

    public string? SchedulingMode { get; init; }

    public int WeeklyMinutes { get; init; }

    public IReadOnlyList<int> DailyMinutes { get; init; } = [0, 0, 0, 0, 0, 0, 0];

    public IReadOnlyList<string> Notes { get; init; } = ["", "", "", "", "", "", ""];
}

public sealed class RoleScheduleState
{
    public required string RoleUid { get; init; }

    public string? RoleName { get; init; }

    public string? ResourceId { get; init; }

    public string? ResourceUid { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public IReadOnlyList<RoleWeekState> Weeks { get; init; } = [];
}

public sealed class SaveProjectRoleRequest
{
    public required string ProjectCode { get; init; }

    public required string RoleName { get; init; }

    /// <summary>ResourceReferenceSystemId when known; else ResourceUid.</summary>
    public string? ResourceReferenceSystemId { get; init; }

    public string? ResourceUid { get; init; }

    public string DefaultSchedulingMode { get; init; } = "W";
}

public sealed class SaveProjectRoleResult
{
    public required string RoleUid { get; init; }

    public string? RoleName { get; init; }
}

public sealed class SaveProjectTaskRoleRequest
{
    public required string RoleUid { get; init; }

    public required string TaskUid { get; init; }

    public int EffortMinutes { get; init; }
}

public sealed class BookRoleHoursRequest
{
    public required string RoleUid { get; init; }

    public IReadOnlyList<RoleHoursBucket> HoursBuckets { get; init; } = [];

    public IReadOnlyList<RoleHoursBucket> NotesBuckets { get; init; } = [];
}
