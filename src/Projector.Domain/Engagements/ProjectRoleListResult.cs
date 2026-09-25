namespace Projector.Domain.Engagements;

/// <summary>PwsGetProjectRoles page plus Projector RowCountExceeded flag.</summary>
public sealed class ProjectRoleListResult
{
    public required IReadOnlyList<ProjectRoleAssignment> Roles { get; init; }

    public bool ServerTruncated { get; init; }
}

/// <summary>
/// PwsGetResourceSchedulingRoleData fan-out result plus truncation / failed project codes.
/// </summary>
public sealed class ProjectBookingListResult
{
    public required IReadOnlyList<ProjectBookingRow> Bookings { get; init; }

    public bool ServerTruncated { get; init; }

    public IReadOnlyList<string> FailedProjectCodes { get; init; } = [];
}
