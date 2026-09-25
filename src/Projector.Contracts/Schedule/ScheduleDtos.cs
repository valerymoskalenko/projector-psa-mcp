using System.Text.Json.Serialization;

namespace Projector.Contracts.Schedule;

public sealed record ResourceScheduleDto(
    IReadOnlyList<ScheduleDateDto> Dates,
    IReadOnlyList<ScheduleHolidayDto> Holidays,
    IReadOnlyList<ScheduleTimeOffDto> TimeOff,
    IReadOnlyList<ScheduleRoleDto> Roles,
    IReadOnlyList<ScheduleBookingDto> Bookings);

public sealed record ScheduleDateDto(
    string? Date,
    int NormalWorkingMinutes,
    double NormalWorkingHours,
    int UtilizationBasisMinutes,
    double UtilizationBasisHours);

public sealed record ScheduleHolidayDto(
    string? Date,
    string? HolidayName,
    int TimeOffMinutes,
    double TimeOffHours);

public sealed record ScheduleTimeOffDto(
    string? Date,
    string? TimeOffReason,
    int TimeOffMinutes,
    double TimeOffHours);

public sealed record ScheduleRoleDto(
    string? RoleName,
    string? RoleStartDate,
    string? RoleEndDate,
    string? ProjectCode,
    string? ProjectName,
    string? ProjectStage,
    string? ProjectOpenDate,
    string? ProjectCloseDate,
    string? ColorMapColor,
    string? EngagementCode,
    string? EngagementName,
    string? ClientName,
    string? ClientNumber,
    string? ProjectManagerDisplayName,
    string? ProjectManagerEmail,
    string? EngagementManagerDisplayName,
    string? EngagementManagerEmail,
    string? ContractTypeName,
    string? ContractTypeShortName,
    bool Billable,
    bool Productive);

public sealed record ScheduleBookingDto(
    string? ProjectCode,
    string? ProjectName,
    string? RoleName,
    string? BookingStatus,
    string? DailyWeeklyFlag,
    string? SchedulingMode,
    string? Date,
    int ScheduledMinutes,
    double ScheduledHours,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Notes = null);
