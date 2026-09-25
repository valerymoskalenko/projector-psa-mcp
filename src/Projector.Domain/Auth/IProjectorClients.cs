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

public interface IProjectorTimecardClient
{
    Task<TimecardListResult> ListTimecardsAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        string? projectCode = null,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<TimeOffListResult> ListTimeOffCardsAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default);
}

public interface IProjectorScheduleClient
{
    Task<ResourceSchedule> GetResourceScheduleAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
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
