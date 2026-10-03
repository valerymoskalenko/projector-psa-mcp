using Projector.Domain.Schedule;

namespace Projector.Application.Tools;

/// <summary>
/// What the schedule expects on one date. Projector already sets the utilization basis to 0 on holidays and PTO days
/// (seen 2026-10-03: Labour Day 1,440 holiday minutes and PTO 480 minutes, basis 0 on both), so the basis is the
/// expected posting time as is.
/// </summary>
/// <param name="NonWorking">No normal working time that day (weekend or a day off in the resource's calendar).</param>
internal sealed record ExpectedDay(string Date, double ExpectedHours, bool NonWorking, string? Holiday, double PtoHours);

internal static class ExpectedHours
{
    /// <summary>The schedule's dates by date (yyyy-MM-dd); dates the schedule doesn't list are not in the result.</summary>
    public static IReadOnlyDictionary<string, ExpectedDay> FromSchedule(ResourceSchedule schedule)
    {
        var holidays = schedule.Holidays
            .Where(h => !string.IsNullOrWhiteSpace(h.Date))
            .GroupBy(h => h.Date!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(h => h.HolidayName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()),
                StringComparer.Ordinal);
        var pto = schedule.TimeOff
            .Where(t => !string.IsNullOrWhiteSpace(t.Date))
            .GroupBy(t => t.Date!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.TimeOffMinutes) / 60.0, StringComparer.Ordinal);

        var result = new Dictionary<string, ExpectedDay>(StringComparer.Ordinal);
        foreach (var d in schedule.Dates.Where(d => !string.IsNullOrWhiteSpace(d.Date)))
        {
            var date = d.Date!;
            var holiday = holidays.TryGetValue(date, out var name) ? (name.Length == 0 ? "holiday" : name) : null;
            result[date] = new ExpectedDay(
                date,
                Math.Max(0, d.UtilizationBasisMinutes) / 60.0,
                d.NormalWorkingMinutes <= 0,
                holiday,
                pto.GetValueOrDefault(date));
        }

        return result;
    }

    /// <summary>Project close dates by project code (upper case), from the schedule's roles; empty dates are left out.</summary>
    public static IReadOnlyDictionary<string, string> ProjectCloseDates(ResourceSchedule schedule) =>
        schedule.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r.ProjectCode) && !string.IsNullOrWhiteSpace(r.ProjectCloseDate))
            .GroupBy(r => r.ProjectCode!.ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(r => r.ProjectCloseDate!)!, StringComparer.Ordinal);
}
