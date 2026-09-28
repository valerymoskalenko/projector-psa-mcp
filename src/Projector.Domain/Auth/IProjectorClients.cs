using Projector.Domain.Auth;
using Projector.Domain.Availability;
using Projector.Domain.Engagements;
using Projector.Domain.Holidays;
using Projector.Domain.Resources;
using Projector.Domain.Schedule;
using Projector.Domain.Timecards;
using Projector.Domain.TimeOff;
using Projector.Domain.Users;

namespace Projector.Domain.Auth;

public interface IProjectorTokenClient
{
    Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    Task<ProjectorTokenResponse> RefreshAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default);
}

public interface IProjectorResourceClient
{
    Task<ResourceListResult> ListResourcesAsync(
        ProjectorConnection connection,
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken = default);

    Task<ResourceDetail?> GetResourceAsync(
        ProjectorConnection connection,
        string id,
        bool includeHistory,
        bool includeUdfs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Focused SOAP method groups beyond resources. Implementations live in ApiClient;
/// MCP tools / application services will call these next.
/// </summary>
public interface IProjectorSoapClient :
    IProjectorResourceClient,
    IProjectorUserClient,
    IProjectorTimecardClient,
    IProjectorScheduleClient,
    IProjectorEngagementClient,
    IProjectorHolidayClient
{
}

/// <summary>A null resource means the signed-in user (Projector applies the call to the caller).</summary>
public interface IProjectorTimecardClient
{
    Task<TimecardListResult> ListTimecardsAsync(
        ProjectorConnection connection,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate,
        string? projectCode = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<TimeOffListResult> ListTimeOffCardsAsync(
        ProjectorConnection connection,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Time entry for the signed-in user only: no method takes a resource, so Projector falls back to the caller.
/// The only write is <see cref="SaveTimecardAsync"/>, which never submits.
/// </summary>
public interface IProjectorTimeEntryClient
{
    Task<IReadOnlyList<TimeEntryProjectSummary>> SearchTimeEntryProjectsAsync(
        ProjectorConnection connection,
        string workDate,
        string? query = null,
        string? projectCode = null,
        CancellationToken cancellationToken = default);

    Task<TimeEntryProjectSetup?> GetTimeEntryProjectAsync(
        ProjectorConnection connection,
        string projectCode,
        string workDate,
        CancellationToken cancellationToken = default);

    Task<TimeEntryParameters> GetTimeEntryParametersAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>Task → assigned roles for one project (PwsGetProject with sub-entities; large on big projects).</summary>
    Task<TaskAssignments> GetTaskAssignmentsAsync(
        ProjectorConnection connection,
        string projectCode,
        CancellationToken cancellationToken = default);

    /// <summary>The caller's own work cards (any status) on one date.</summary>
    Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
        ProjectorConnection connection,
        string workDate,
        CancellationToken cancellationToken = default);

    Task<OwnTimecard?> GetOwnTimecardAsync(
        ProjectorConnection connection,
        string timecardUid,
        string workDate,
        CancellationToken cancellationToken = default);

    /// <summary>Not retried: a timeout or transport error throws <c>write_outcome_unknown</c>.</summary>
    Task<TimecardSaveResult> SaveTimecardAsync(
        ProjectorConnection connection,
        TimecardSaveRequest request,
        CancellationToken cancellationToken = default);
}

public interface IProjectorScheduleClient
{
    Task<ResourceSchedule> GetResourceScheduleAsync(
        ProjectorConnection connection,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default);

    Task<AvailabilitySummary> CheckAvailabilityAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        string? displayName = null,
        string? emailAddress = null,
        double requiredMinutesPerWeek = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HolidayEntry>> GetResourcePtoHolidaysAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string cutoffDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UtilizationYear>> GetUtilizationAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        CancellationToken cancellationToken = default);
}

public interface IProjectorEngagementClient
{
    Task<EngagementListResult> ListEngagementsAsync(
        ProjectorConnection connection,
        string? query,
        bool includeClosed,
        int maxRows,
        CancellationToken cancellationToken = default);

    Task<EngagementDetail?> GetEngagementAsync(
        ProjectorConnection connection,
        string engagementCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EngagementDetail>> GetEngagementsByCodeAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> engagementCodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectSummary>> GetProjectsByCodeAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        CancellationToken cancellationToken = default);

    Task<ProjectRoleListResult> ListProjectRolesAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        CancellationToken cancellationToken = default);

    Task<ProjectBookingListResult> ListProjectBookingsAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default);
}

public interface IProjectorHolidayClient
{
    Task<CompanyHolidayCalendarsResult> GetCompanyHolidayCalendarsAsync(
        ProjectorConnection connection,
        string startDate,
        string endDate,
        string? location = null,
        int maxRows = 10000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTimeOffExportRow>> ListScheduledTimeOffExportAsync(
        ProjectorConnection connection,
        string dateBookmark,
        int maxRows = 10000,
        CancellationToken cancellationToken = default);
}

public interface IProjectorUserClient
{
    Task<IReadOnlyList<UserSummary>> ListUsersAsync(
        ProjectorConnection connection,
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken = default);

    Task<UserSummary?> GetUserAsync(
        ProjectorConnection connection,
        string id,
        CancellationToken cancellationToken = default);
}
