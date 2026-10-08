using Projector.Domain.Auth;

namespace Projector.Domain.Bookings;

/// <summary>
/// Scheduler-mode booking writes and the role schedule read used to plan them. Nothing here requests, submits or
/// finalizes a role.
/// </summary>
public interface IProjectorBookingClient
{
    /// <summary>Booked hours and notes for every role on one project, starting at <paramref name="startDate"/>.</summary>
    Task<IReadOnlyList<RoleScheduleState>> GetRoleSchedulesAsync(
        ProjectorConnection connection,
        string projectCode,
        string startDate,
        int minimumWeekCount,
        CancellationToken cancellationToken = default);

    /// <summary>Creates one project role with a named resource. Sent once, never retried.</summary>
    Task<SaveProjectRoleResult> SaveProjectRoleAsync(
        ProjectorConnection connection,
        SaveProjectRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Assigns a role to a task (effort left at 0 unless set). Sent once, never retried.</summary>
    Task SaveProjectTaskRoleAsync(
        ProjectorConnection connection,
        SaveProjectTaskRoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Books hours and notes on an existing role (Mode=A). No finalize. Sent once, never retried; a timeout throws
    /// write_outcome_unknown.
    /// </summary>
    Task BookRoleHoursAsync(
        ProjectorConnection connection,
        BookRoleHoursRequest request,
        CancellationToken cancellationToken = default);
}
