using System.Text.Json.Serialization;
using Projector.Contracts.Common;

namespace Projector.Contracts.Holidays;

public sealed record HolidayEntryDto(
    string? Date,
    string? HolidayName,
    int TimeOffMinutes,
    double TimeOffHours);

public sealed record HolidayCalendarDto(
    string? Location,
    IReadOnlyList<HolidayEntryDto> Holidays);

public sealed record ListHolidaysResponse(
    [property: JsonPropertyName("start_date")] string? StartDate,
    [property: JsonPropertyName("end_date")] string? EndDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    IReadOnlyList<HolidayCalendarDto> Calendars,
    int Count,
    [property: JsonPropertyName("active_resource_count")] int ActiveResourceCount,
    SearchCoverageDto SearchCoverage);
