using System.Text.Json.Serialization;
using Projector.Contracts.Schedule;

namespace Projector.Contracts.Availability;

public sealed record AvailabilitySummaryDto(
    string? ResourceReferenceSystemId,
    string? DisplayName,
    string? EmailAddress,
    string State,
    string CapacityBasis,
    IReadOnlyList<AvailabilityDayDto> Days,
    IReadOnlyList<AvailabilityWeekDto> Weeks,
    IReadOnlyList<ScheduleBookingDto> Bookings,
    IReadOnlyList<ScheduleRoleDto> Roles,
    IReadOnlyList<ScheduleHolidayDto> Holidays,
    IReadOnlyList<ScheduleTimeOffDto> TimeOff);

public sealed record AvailabilityDayDto(
    string? Date,
    int NormalWorkingMinutes,
    double NormalWorkingHours,
    int UtilizationBasisMinutes,
    double UtilizationBasisHours,
    int BookedMinutes,
    double BookedHours,
    int PtoMinutes,
    double PtoHours,
    int HolidayMinutes,
    double HolidayHours,
    int AvailableMinutes,
    double AvailableHours,
    int OverallocatedMinutes,
    double OverallocatedHours,
    string State);

public sealed record AvailabilityWeekDto(
    string? WeekStart,
    int UtilizationBasisMinutes,
    double UtilizationBasisHours,
    int NormalWorkingMinutes,
    double NormalWorkingHours,
    int BookedMinutes,
    double BookedHours,
    int PtoMinutes,
    double PtoHours,
    int HolidayMinutes,
    double HolidayHours,
    int AvailableMinutes,
    double AvailableHours,
    int OverallocatedMinutes,
    double OverallocatedHours,
    int RequiredMinutes,
    double RequiredHours,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MeetsRequiredCapacity,
    int ShortfallMinutes,
    double ShortfallHours,
    int SurplusMinutes,
    double SurplusHours);

public sealed record DailyBookingDto(
    string? Date,
    string? ProjectCode,
    string? ProjectName,
    string? RoleName,
    string? BookingStatus,
    int BookedMinutes);
