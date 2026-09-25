namespace Projector.Domain.Schedule;

public sealed class ResourceSchedule
{
    public IReadOnlyList<ScheduleDate> Dates { get; init; } = [];

    public IReadOnlyList<ScheduleHoliday> Holidays { get; init; } = [];

    public IReadOnlyList<ScheduleTimeOff> TimeOff { get; init; } = [];

    public IReadOnlyList<ScheduleRole> Roles { get; init; } = [];

    public IReadOnlyList<ScheduleBooking> Bookings { get; init; } = [];
}

public sealed class ScheduleDate
{
    public string? Date { get; init; }

    public int NormalWorkingMinutes { get; init; }

    public double NormalWorkingHours { get; init; }

    public int UtilizationBasisMinutes { get; init; }

    public double UtilizationBasisHours { get; init; }
}

public sealed class ScheduleHoliday
{
    public string? Date { get; init; }

    public string? HolidayName { get; init; }

    public int TimeOffMinutes { get; init; }

    public double TimeOffHours { get; init; }
}

public sealed class ScheduleTimeOff
{
    public string? Date { get; init; }

    public string? TimeOffReason { get; init; }

    public int TimeOffMinutes { get; init; }

    public double TimeOffHours { get; init; }
}

public sealed class ScheduleRole
{
    public string? RoleName { get; init; }

    public string? RoleStartDate { get; init; }

    public string? RoleEndDate { get; init; }

    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? ProjectStage { get; init; }

    public string? ProjectOpenDate { get; init; }

    public string? ProjectCloseDate { get; init; }

    public string? ColorMapColor { get; init; }

    public string? EngagementCode { get; init; }

    public string? EngagementName { get; init; }

    public string? ClientName { get; init; }

    public string? ClientNumber { get; init; }

    public string? ProjectManagerDisplayName { get; init; }

    public string? ProjectManagerEmail { get; init; }

    public string? EngagementManagerDisplayName { get; init; }

    public string? EngagementManagerEmail { get; init; }

    public string? ContractTypeName { get; init; }

    public string? ContractTypeShortName { get; init; }

    public bool Billable { get; init; }

    public bool Productive { get; init; }
}

public sealed class ScheduleBooking
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? RoleName { get; init; }

    public string? BookingStatus { get; init; }

    public string? DailyWeeklyFlag { get; init; }

    public string? SchedulingMode { get; init; }

    public string? Date { get; init; }

    public int ScheduledMinutes { get; init; }

    public double ScheduledHours { get; init; }

    public IReadOnlyList<string>? Notes { get; init; }
}
