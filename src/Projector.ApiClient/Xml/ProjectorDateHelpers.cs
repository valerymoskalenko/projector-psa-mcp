using System.Globalization;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

public static class ProjectorDateHelpers
{
    public static string ToSoapDate(string date)
    {
        var trimmed = date.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{4}-\d{2}-\d{2}$"))
        {
            return $"{trimmed}T00:00:00.000Z";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{4}-\d{2}-\d{2}T"))
        {
            return trimmed;
        }

        var parsed = DateTime.Parse(trimmed, CultureInfo.InvariantCulture);
        return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z";
    }

    public static string ToShortDate(string date) => ToSoapDate(date)[..10];

    public static int GetDateRangeDays(string startDate, string endDate)
    {
        var start = DateTime.ParseExact(ToShortDate(startDate), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(ToShortDate(endDate), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (end < start)
        {
            throw new ProjectorApiException($"end_date '{endDate}' is before start_date '{startDate}'.", "InvalidDateRange");
        }

        return (int)(end - start).TotalDays + 1;
    }

    public static void AssertDateWindow(string startDate, string endDate, int maxDays = 56)
    {
        var days = GetDateRangeDays(startDate, endDate);
        if (days > maxDays)
        {
            throw new ProjectorApiException(
                $"Date range spans {days} days; maximum allowed is {maxDays} (~8 weeks).",
                "DateWindowExceeded");
        }
    }

    public static double MinutesAsHours(double? minutes)
    {
        var mins = minutes ?? 0;
        return mins / 60.0;
    }

    /// <summary>
    /// Inclusive calendar window → MinimumWeekCount for PwsGetResourceSchedulingRoleData.
    /// </summary>
    public static int GetMinimumWeekCountForWindow(string startDate, string endDate)
    {
        var days = GetDateRangeDays(startDate, endDate);
        return Math.Max(1, (int)Math.Ceiling(days / 7.0));
    }

    public static string? GetWeekStart(string date)
    {
        var dt = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Weeks start Sunday to match the Windows client grid.
        var offset = (int)dt.DayOfWeek;
        return dt.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
