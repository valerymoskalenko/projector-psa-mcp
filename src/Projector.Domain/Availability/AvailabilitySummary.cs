using System.Text.Json.Serialization;
using Projector.Domain.Schedule;

namespace Projector.Domain.Availability;

public sealed class AvailabilitySummary
{
    public string? ResourceReferenceSystemId { get; init; }

    public string? DisplayName { get; init; }

    public string? EmailAddress { get; init; }

    public string State { get; init; } = "non_working";

    public string CapacityBasis { get; init; } = "utilization";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AvailabilityDay>? Days { get; init; } = [];

    public IReadOnlyList<AvailabilityWeek> Weeks { get; init; } = [];

    /// <summary>
    /// Projector-native schedule bookings (daily and weekly rows), not exploded daily slices.
    /// Expansion is used only for day/week capacity math.
    /// </summary>
    public IReadOnlyList<ScheduleBooking> Bookings { get; init; } = [];

    public IReadOnlyList<ScheduleRole> Roles { get; init; } = [];

    public IReadOnlyList<ScheduleHoliday> Holidays { get; init; } = [];

    public IReadOnlyList<ScheduleTimeOff> TimeOff { get; init; } = [];

    public AvailabilitySummary WithoutDays() => new()
    {
        ResourceReferenceSystemId = ResourceReferenceSystemId,
        DisplayName = DisplayName,
        EmailAddress = EmailAddress,
        State = State,
        CapacityBasis = CapacityBasis,
        Days = null,
        Weeks = Weeks,
        Bookings = Bookings,
        Roles = Roles,
        Holidays = Holidays,
        TimeOff = TimeOff
    };
}

public sealed class AvailabilityDay
{
    public string? Date { get; init; }

    public int NormalWorkingMinutes { get; init; }

    public double NormalWorkingHours { get; init; }

    public int UtilizationBasisMinutes { get; init; }

    public double UtilizationBasisHours { get; init; }

    public int BookedMinutes { get; init; }

    public double BookedHours { get; init; }

    public int PtoMinutes { get; init; }

    public double PtoHours { get; init; }

    public int HolidayMinutes { get; init; }

    public double HolidayHours { get; init; }

    public int AvailableMinutes { get; init; }

    public double AvailableHours { get; init; }

    public int OverallocatedMinutes { get; init; }

    public double OverallocatedHours { get; init; }

    public string State { get; init; } = "non_working";
}

public sealed class AvailabilityWeek
{
    public string? WeekStart { get; init; }

    public int UtilizationBasisMinutes { get; init; }

    public double UtilizationBasisHours { get; init; }

    public int NormalWorkingMinutes { get; init; }

    public double NormalWorkingHours { get; init; }

    public int BookedMinutes { get; init; }

    public double BookedHours { get; init; }

    public int PtoMinutes { get; init; }

    public double PtoHours { get; init; }

    public int HolidayMinutes { get; init; }

    public double HolidayHours { get; init; }

    public int AvailableMinutes { get; init; }

    public double AvailableHours { get; init; }

    public int OverallocatedMinutes { get; init; }

    public double OverallocatedHours { get; init; }

    public int RequiredMinutes { get; init; }

    public double RequiredHours { get; init; }

    public bool? MeetsRequiredCapacity { get; init; }

    public int ShortfallMinutes { get; init; }

    public double ShortfallHours { get; init; }

    public int SurplusMinutes { get; init; }

    public double SurplusHours { get; init; }
}

public sealed class DailyBooking
{
    public string? Date { get; init; }

    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? RoleName { get; init; }

    public string? BookingStatus { get; init; }

    public int BookedMinutes { get; init; }
}
