namespace Projector.Domain.Holidays;

public sealed class HolidayEntry
{
    public string? Date { get; init; }

    public string? HolidayName { get; init; }

    public int TimeOffMinutes { get; init; }

    public double TimeOffHours { get; init; }
}

public sealed class HolidayCalendar
{
    public string? Location { get; init; }

    public IReadOnlyList<HolidayEntry> Holidays { get; init; } = [];
}

public sealed class ExportedResourceRow
{
    public string? ResourceReferenceSystemId { get; init; }

    public string? ResourceId { get; init; }

    public string? DisplayName { get; init; }

    public string? LocationName { get; init; }

    public string? BeginDate { get; init; }

    public string? EndDate { get; init; }

    public bool Inactive { get; init; }
}

public sealed class HolidayCalendarResource
{
    public string? ResourceReferenceSystemId { get; init; }

    public string? DisplayName { get; init; }

    public string? LocationName { get; init; }

    public bool LocationChanged { get; init; }
}

public sealed class CompanyHolidayCalendarsResult
{
    public string? StartDate { get; init; }

    public string? EndDate { get; init; }

    public string? Location { get; init; }

    public IReadOnlyList<HolidayCalendar> Calendars { get; init; } = [];

    public int Count { get; init; }

    public int ActiveResourceCount { get; init; }

    public required Pagination.SearchCoverage SearchCoverage { get; init; }
}
