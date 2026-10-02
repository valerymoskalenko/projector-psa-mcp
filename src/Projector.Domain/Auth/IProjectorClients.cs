using Projector.Domain.Auth;
using Projector.Domain.Availability;
using Projector.Domain.Engagements;
using Projector.Domain.Holidays;
using Projector.Domain.Reports;
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

    /// <summary>The caller's own work cards (any status) from <paramref name="startDate"/> through <paramref name="endDate"/>.</summary>
    Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
        ProjectorConnection connection,
        string startDate,
        string endDate,
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

    /// <summary>A null resource = the caller (PwsGetResourceSchedule without ResourceIdentity).</summary>
    Task<AvailabilitySummary> CheckAvailabilityAsync(
        ProjectorConnection connection,
        string? resourceReferenceSystemId,
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

    /// <summary>One project's task plan (tasks, planned dates, effort per role); null when the project is not found.</summary>
    Task<ProjectTaskPlan?> GetProjectTaskPlanAsync(
        ProjectorConnection connection,
        string projectCode,
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

/// <summary>
/// Large, cross-person reads for get_report: the user's saved reports and the legacy exports.
/// Starting a report run or an export batch is not retried; it changes no business data.
/// </summary>
public interface IProjectorReportClient
{
    /// <summary>A saved report's stored output as a table (CSV with a header row), by web service code or output UID.</summary>
    Task<ReportTable> GetReportOutputAsync(
        ProjectorConnection connection,
        string? webServiceCode,
        string? outputUid,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the caller's own saved report; returns the new output UID.</summary>
    Task<string> SubmitReportSpecAsync(
        ProjectorConnection connection,
        string specUid,
        CancellationToken cancellationToken = default);

    /// <summary>One run by output UID, or the caller's recent runs (without UIDs) when <paramref name="outputUid"/> is null.</summary>
    Task<IReadOnlyList<ReportRun>> GetReportStatusAsync(
        ProjectorConnection connection,
        string? outputUid,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a Ginsu batch export; returns its request id.</summary>
    Task<string> SubmitGinsuExportAsync(
        ProjectorConnection connection,
        GinsuExportRequest request,
        CancellationToken cancellationToken = default);

    Task<BatchPage> GetGinsuRecordsAsync(
        ProjectorConnection connection,
        string requestId,
        long startAfterRowIndex,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default);

    Task<ExportPage> ExportProjectListAsync(
        ProjectorConnection connection,
        bool openForTimeOnly,
        string? projectCodesAfter,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default);

    /// <summary>Approved time cards in a work-date range, continued after the last card's approval time and id.</summary>
    Task<ExportPage> ExportTimeCardsAsync(
        ProjectorConnection connection,
        string minWorkDate,
        string maxWorkDate,
        string? approvedMinTimestamp,
        string? approvedIdsAfter,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default);

    /// <summary>Resource reference id → display name (ExportResources), for turning ids in exports into names.</summary>
    Task<IReadOnlyDictionary<string, string>> ExportResourceNamesAsync(
        ProjectorConnection connection,
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
