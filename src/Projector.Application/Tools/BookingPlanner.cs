using System.Globalization;
using Projector.Domain.Bookings;

namespace Projector.Application.Tools;

/// <summary>
/// Pure planning for save_booking: which weeks to include, how to spread hours, and how extra days turn a week into
/// daily minutes. Does not call Projector.
/// </summary>
public static class BookingPlanner
{
    public const int MaxMinutes = short.MaxValue;

    public sealed record ExtraDay(string Date, double Hours, string? Comment);

    public sealed record CommentDay(string Date, string Text);

    public sealed record PlannedWeek(
        string WeekStart,
        string SchedulingMode,
        int? WeeklyMinutes,
        IReadOnlyList<int>? DailyMinutes,
        IReadOnlyList<string>? Notes,
        int PreviousMinutes,
        int NewMinutes,
        bool NotesChanged);

    public sealed record PlanResult(
        IReadOnlyList<PlannedWeek> Weeks,
        IReadOnlyList<string> SkippedWeeks,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Plans the weeks to write. With <paramref name="hoursPerPeriod"/> each planned week is replaced (weekly total,
    /// or hours on each working day). Without it the current amounts stay: extra days are added on top and comments
    /// change only the notes, and only the weeks they touch are written.
    /// </summary>
    public static PlanResult Plan(
        string startDate,
        string endDate,
        string schedulingMode,
        double? hoursPerPeriod,
        IReadOnlyList<ExtraDay>? extraDays,
        IReadOnlyList<CommentDay>? comments,
        IReadOnlyDictionary<string, RoleWeekState> currentByWeek)
    {
        var errors = new List<string>();
        if (!TryParseDate(startDate, out var start))
        {
            errors.Add("start_date must be yyyy-MM-dd.");
        }

        if (!TryParseDate(endDate, out var end))
        {
            errors.Add("end_date must be yyyy-MM-dd.");
        }

        if (errors.Count == 0 && end < start)
        {
            errors.Add("end_date must be on or after start_date.");
        }

        var mode = schedulingMode.Trim().ToLowerInvariant();
        if (mode is not ("daily" or "weekly"))
        {
            errors.Add("scheduling_mode must be daily or weekly.");
        }

        if (hoursPerPeriod < 0)
        {
            errors.Add("hours must be zero or greater.");
        }

        var periodMinutes = hoursPerPeriod is null
            ? (int?)null
            : (int)Math.Round(hoursPerPeriod.Value * 60.0, MidpointRounding.AwayFromZero);
        if (periodMinutes > MaxMinutes)
        {
            errors.Add($"hours is too large for Projector (max {MaxMinutes / 60.0:0} hours on one day or week).");
        }

        var extras = NormalizeExtras(extraDays, errors);
        var noteEdits = NormalizeComments(comments, extras, errors);
        if (errors.Count == 0 && periodMinutes is null && extras.Count == 0 && noteEdits.Count == 0)
        {
            errors.Add(
                "Nothing to change: give hours (replaces each week), or extra_days / comments (keep the current " +
                "hours and add on top).");
        }

        if (errors.Count > 0)
        {
            return new PlanResult([], [], errors);
        }

        var skipped = new List<string>();
        var planned = new List<PlannedWeek>();
        var weekStarts = WeekStartsInRange(start, end, mode == "weekly", skipped);

        // A day the call names must land in a week that is written, or it would be dropped without a word.
        var plannedWeeks = new HashSet<string>(weekStarts, StringComparer.Ordinal);
        var namedDays = extras.Select(e => (Field: "extra_days", e.Date))
            .Concat(noteEdits.Select(n => (Field: "comments", n.Date)));
        foreach (var (field, date) in namedDays)
        {
            var day = ParseDate(date);
            var week = WeekStartOf(day);
            if (day < start || day > end)
            {
                errors.Add($"{field} date {date} is outside start_date..end_date ({Format(start)}..{Format(end)}).");
            }
            else if (!plannedWeeks.Contains(Format(week)))
            {
                errors.Add(
                    $"{field} date {date} is in the week {Format(week)}..{Format(week.AddDays(6))}, which runs past " +
                    $"end_date {Format(end)}: weekly mode books full Sunday–Saturday weeks only. Move end_date to " +
                    "the Saturday or use scheduling_mode daily.");
            }
        }

        if (errors.Count > 0)
        {
            return new PlanResult([], skipped, errors);
        }

        foreach (var weekStart in weekStarts)
        {
            currentByWeek.TryGetValue(weekStart, out var current);
            var weekExtras = extras.Where(e => Format(WeekStartOf(e.Date)) == weekStart).ToList();
            var weekNotes = noteEdits.Where(n => Format(WeekStartOf(n.Date)) == weekStart).ToList();
            if (periodMinutes is null && weekExtras.Count == 0 && weekNotes.Count == 0)
            {
                // Keep-current mode: a week the call does not touch is not written.
                continue;
            }

            var needsDaily = weekExtras.Count > 0 || (periodMinutes is not null && mode == "daily");
            var sunday = ParseDate(weekStart);
            var saturday = sunday.AddDays(6);
            var weekFullyInside = sunday >= start && saturday <= end;
            var currentIsWeekly = string.Equals(current?.SchedulingMode, "W", StringComparison.OrdinalIgnoreCase);
            var currentIsDaily = string.Equals(current?.SchedulingMode, "D", StringComparison.OrdinalIgnoreCase);

            if (needsDaily && currentIsWeekly && !weekFullyInside && weekExtras.Count == 0)
            {
                errors.Add(
                    $"Week {weekStart} is stored as a weekly total and the date range does not cover the whole " +
                    "Sunday–Saturday week. Widen the range to the full week, or book only full weeks.");
                continue;
            }

            var previousMinutes = current?.WeeklyMinutes ?? 0;

            int[]? daily = null;
            int? weekly = null;
            string outMode;

            if (periodMinutes is null && weekExtras.Count == 0)
            {
                // Notes only: the hours stay exactly as stored.
                if (currentIsDaily)
                {
                    outMode = "D";
                    daily = current!.DailyMinutes.ToArray();
                }
                else
                {
                    outMode = "W";
                    weekly = current?.WeeklyMinutes ?? 0;
                }
            }
            else if (needsDaily)
            {
                outMode = "D";
                daily = periodMinutes is null
                    ? BuildDailyFromCurrent(current, weekExtras)
                    : BuildDailyMinutes(weekStart, start, end, mode, periodMinutes.Value, weekExtras, current);
            }
            else
            {
                outMode = "W";
                weekly = periodMinutes!.Value;
            }

            var newMinutes = daily?.Sum() ?? weekly!.Value;
            if (newMinutes > MaxMinutes || (daily is not null && daily.Any(m => m > MaxMinutes)))
            {
                errors.Add($"Week {weekStart}: minutes exceed Projector's limit of {MaxMinutes}.");
                continue;
            }

            string[]? notes = null;
            var notesChanged = false;
            if (weekNotes.Count > 0 || weekExtras.Any(e => e.Comment is not null))
            {
                notes = MergeNotes(weekStart, current?.Notes, weekNotes, weekExtras);
                notesChanged = true;
            }

            planned.Add(new PlannedWeek(
                weekStart, outMode, weekly, daily, notes, previousMinutes, newMinutes, notesChanged));
        }

        if (planned.Count == 0 && errors.Count == 0)
        {
            errors.Add(mode == "weekly"
                ? "No full Sunday–Saturday week ends on or before end_date. Widen the range or use scheduling_mode daily."
                : "No days in the date range.");
        }

        return new PlanResult(planned, skipped, errors);
    }

    /// <summary>
    /// Keep-current mode with extra days: the stored daily amounts (or the stored weekly total spread over Mon–Fri)
    /// plus the extras.
    /// </summary>
    internal static int[] BuildDailyFromCurrent(RoleWeekState? current, IReadOnlyList<ExtraDay> weekExtras)
    {
        var daily = new int[7];
        if (string.Equals(current?.SchedulingMode, "D", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < 7; i++)
            {
                daily[i] = current!.DailyMinutes.ElementAtOrDefault(i);
            }
        }
        else if (current is not null)
        {
            SpreadOverWorkdays(daily, current.WeeklyMinutes);
        }

        AddExtras(daily, weekExtras);
        return daily;
    }

    private static void SpreadOverWorkdays(int[] daily, int weekMinutes)
    {
        var basePerDay = weekMinutes / 5;
        var remainder = weekMinutes % 5;
        for (var i = 1; i <= 5; i++)
        {
            daily[i] = basePerDay + (i == 5 ? remainder : 0);
        }
    }

    private static void AddExtras(int[] daily, IReadOnlyList<ExtraDay> weekExtras)
    {
        foreach (var extra in weekExtras)
        {
            var index = (int)ParseDate(extra.Date).DayOfWeek;
            daily[index] = checked(daily[index] + (int)Math.Round(extra.Hours * 60.0, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Weekly: weeks whose Saturday is on or before end. Daily: every overlapping week.</summary>
    internal static IReadOnlyList<string> WeekStartsInRange(
        DateTime start, DateTime end, bool weeklyOnlyFull, List<string> skipped)
    {
        var firstWeek = WeekStartOf(start);
        var lastPossible = WeekStartOf(end);
        var list = new List<string>();
        for (var week = firstWeek; week <= lastPossible; week = week.AddDays(7))
        {
            var saturday = week.AddDays(6);
            if (weeklyOnlyFull && saturday > end)
            {
                skipped.Add(
                    $"{Format(week)}..{Format(saturday)} not booked: the week runs past end_date {Format(end)} " +
                    "(weekly mode books full Sunday–Saturday weeks; move end_date to the Saturday or use daily)");
                continue;
            }

            if (saturday < start || week > end)
            {
                continue;
            }

            list.Add(Format(week));
        }

        return list;
    }

    internal static int[] BuildDailyMinutes(
        string weekStart,
        DateTime rangeStart,
        DateTime rangeEnd,
        string mode,
        int periodMinutes,
        IReadOnlyList<ExtraDay> weekExtras,
        RoleWeekState? current)
    {
        var sunday = ParseDate(weekStart);
        var daily = new int[7];

        if (mode == "weekly")
        {
            // Spread the week hours over Mon–Fri, then add extras on top.
            SpreadOverWorkdays(daily, periodMinutes);
        }
        else
        {
            for (var i = 0; i < 7; i++)
            {
                var day = sunday.AddDays(i);
                if (day < rangeStart || day > rangeEnd)
                {
                    daily[i] = current?.DailyMinutes.ElementAtOrDefault(i) ?? 0;
                    continue;
                }

                daily[i] = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : periodMinutes;
            }
        }

        AddExtras(daily, weekExtras);
        return daily;
    }

    internal static string[] MergeNotes(
        string weekStart,
        IReadOnlyList<string>? current,
        IReadOnlyList<CommentDay> comments,
        IReadOnlyList<ExtraDay> extras)
    {
        var notes = new string[7];
        for (var i = 0; i < 7; i++)
        {
            notes[i] = current is not null && i < current.Count ? current[i] ?? string.Empty : string.Empty;
        }

        foreach (var c in comments)
        {
            notes[(int)ParseDate(c.Date).DayOfWeek] = c.Text;
        }

        foreach (var e in extras.Where(e => e.Comment is not null))
        {
            notes[(int)ParseDate(e.Date).DayOfWeek] = e.Comment!;
        }

        return notes;
    }

    private static List<ExtraDay> NormalizeExtras(IReadOnlyList<ExtraDay>? extras, List<string> errors)
    {
        var list = new List<ExtraDay>();
        if (extras is null)
        {
            return list;
        }

        foreach (var e in extras)
        {
            if (!TryParseDate(e.Date, out _))
            {
                errors.Add($"extra_days date '{e.Date}' must be yyyy-MM-dd.");
                continue;
            }

            if (e.Hours < 0)
            {
                errors.Add($"extra_days hours for {e.Date} must be zero or greater.");
                continue;
            }

            if (e.Comment is { Length: > 4000 })
            {
                errors.Add($"extra_days comment for {e.Date} is longer than 4000 characters.");
                continue;
            }

            list.Add(new ExtraDay(Format(ParseDate(e.Date)), e.Hours, e.Comment));
        }

        return list;
    }

    private static List<CommentDay> NormalizeComments(
        IReadOnlyList<CommentDay>? comments,
        IReadOnlyList<ExtraDay> extras,
        List<string> errors)
    {
        var list = new List<CommentDay>();
        if (comments is null)
        {
            return list;
        }

        var extraDates = new HashSet<string>(extras.Select(e => e.Date), StringComparer.Ordinal);
        foreach (var c in comments)
        {
            if (!TryParseDate(c.Date, out var d))
            {
                errors.Add($"comments date '{c.Date}' must be yyyy-MM-dd.");
                continue;
            }

            if (c.Text.Length > 4000)
            {
                errors.Add($"comments text for {c.Date} is longer than 4000 characters.");
                continue;
            }

            var key = Format(d);
            if (extraDates.Contains(key))
            {
                // extra_days.comment wins for the same date.
                continue;
            }

            list.Add(new CommentDay(key, c.Text));
        }

        return list;
    }

    private static DateTime WeekStartOf(DateTime date) => date.AddDays(-(int)date.DayOfWeek);

    private static DateTime WeekStartOf(string date) => WeekStartOf(ParseDate(date));

    private static bool TryParseDate(string? value, out DateTime date) =>
        DateTime.TryParseExact(
            value is { Length: >= 10 } ? value[..10] : value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static DateTime ParseDate(string value) =>
        DateTime.ParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Format(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
