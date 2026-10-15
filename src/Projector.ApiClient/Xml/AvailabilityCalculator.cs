using System.Globalization;
using Projector.Domain.Availability;
using Projector.Domain.Schedule;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Utilization-basis availability math ported from Expand-ProjectorWeeklyBookings /
/// ConvertTo-ProjectorAvailabilitySummary.
/// </summary>
public static class AvailabilityCalculator
{
    /// <summary>
    /// Cuts a schedule read in whole weeks back to start..end (yyyy-MM-dd): dates, holidays, time off and daily
    /// bookings inside the window, weekly bookings of the weeks it touches. The whole weeks stay in
    /// <see cref="ResourceSchedule.WeekDates"/> for spreading weekly bookings.
    /// </summary>
    public static ResourceSchedule TrimToWindow(ResourceSchedule full, string start, string end)
    {
        bool InWindow(string? date) =>
            !string.IsNullOrWhiteSpace(date)
            && string.CompareOrdinal(date, start) >= 0
            && string.CompareOrdinal(date, end) <= 0;

        var firstWeek = ProjectorDateHelpers.GetWeekStart(start)!;
        return new ResourceSchedule
        {
            Dates = full.Dates.Where(d => InWindow(d.Date)).ToList(),
            WeekDates = full.Dates,
            Holidays = full.Holidays.Where(h => InWindow(h.Date)).ToList(),
            TimeOff = full.TimeOff.Where(t => InWindow(t.Date)).ToList(),
            Roles = full.Roles,
            Bookings = full.Bookings.Where(b => IsWeekly(b)
                    ? !string.IsNullOrWhiteSpace(b.Date)
                      && string.CompareOrdinal(b.Date, firstWeek) >= 0
                      && string.CompareOrdinal(b.Date, end) <= 0
                    : InWindow(b.Date))
                .ToList()
        };
    }

    private static bool IsWeekly(ScheduleBooking b)
    {
        var flag = b.DailyWeeklyFlag ?? "";
        return !(flag is "D" or "Daily" || string.IsNullOrWhiteSpace(flag));
    }

    /// <summary>
    /// Expands weekly bookings across the utilization workdays of their week. A week without any (no capacity, or
    /// all holidays) spreads over Monday–Friday, so the hours show as overallocated instead of vanishing. Empty
    /// bookings are OK (PTO-only weeks).
    /// </summary>
    public static IReadOnlyList<DailyBooking> ExpandWeeklyBookings(
        IReadOnlyList<ScheduleBooking> bookings,
        IReadOnlyList<ScheduleDate> scheduleDates)
    {
        var daily = new List<DailyBooking>();

        foreach (var b in bookings)
        {
            if (!IsWeekly(b))
            {
                daily.Add(new DailyBooking
                {
                    Date = b.Date,
                    ProjectCode = b.ProjectCode,
                    ProjectName = b.ProjectName,
                    RoleName = b.RoleName,
                    BookingStatus = b.BookingStatus,
                    BookedMinutes = b.ScheduledMinutes
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(b.Date))
            {
                continue;
            }

            var weekStart = DateTime.ParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var weekEnd = weekStart.AddDays(6);
            var workDays = scheduleDates.Where(d =>
            {
                if (string.IsNullOrWhiteSpace(d.Date) || d.UtilizationBasisMinutes == 0)
                {
                    return false;
                }

                var dt = DateTime.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return dt >= weekStart && dt <= weekEnd;
            }).ToList();

            if (workDays.Count == 0)
            {
                workDays = Enumerable.Range(1, 5)
                    .Select(i => new ScheduleDate
                    {
                        Date = weekStart.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    })
                    .ToList();
            }

            var perDay = b.ScheduledMinutes / workDays.Count;
            var remainder = b.ScheduledMinutes - (perDay * workDays.Count);
            for (var i = 0; i < workDays.Count; i++)
            {
                var mins = perDay;
                if (i == 0)
                {
                    mins += remainder;
                }

                daily.Add(new DailyBooking
                {
                    Date = workDays[i].Date,
                    ProjectCode = b.ProjectCode,
                    ProjectName = b.ProjectName,
                    RoleName = b.RoleName,
                    BookingStatus = b.BookingStatus,
                    BookedMinutes = mins
                });
            }
        }

        return daily;
    }

    public static AvailabilitySummary ToAvailabilitySummary(
        ResourceSchedule schedule,
        string? resourceReferenceSystemId = null,
        string? displayName = null,
        string? emailAddress = null,
        double requiredMinutesPerWeek = 0)
    {
        var dailyBookings = ExpandWeeklyBookings(
            schedule.Bookings, schedule.WeekDates.Count > 0 ? schedule.WeekDates : schedule.Dates);
        var bookedByDate = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in dailyBookings)
        {
            if (string.IsNullOrWhiteSpace(b.Date))
            {
                continue;
            }

            bookedByDate[b.Date] = bookedByDate.GetValueOrDefault(b.Date) + b.BookedMinutes;
        }

        var ptoByDate = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in schedule.TimeOff)
        {
            if (string.IsNullOrWhiteSpace(t.Date))
            {
                continue;
            }

            ptoByDate[t.Date] = ptoByDate.GetValueOrDefault(t.Date) + t.TimeOffMinutes;
        }

        var holidayByDate = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in schedule.Holidays)
        {
            if (string.IsNullOrWhiteSpace(h.Date))
            {
                continue;
            }

            holidayByDate[h.Date] = holidayByDate.GetValueOrDefault(h.Date) + h.TimeOffMinutes;
        }

        var days = schedule.Dates.Select(d =>
        {
            var booked = string.IsNullOrWhiteSpace(d.Date) ? 0 : bookedByDate.GetValueOrDefault(d.Date);
            var pto = string.IsNullOrWhiteSpace(d.Date) ? 0 : ptoByDate.GetValueOrDefault(d.Date);
            var holiday = string.IsNullOrWhiteSpace(d.Date) ? 0 : holidayByDate.GetValueOrDefault(d.Date);
            var capacity = d.UtilizationBasisMinutes;
            var available = Math.Max(0, capacity - booked);
            var over = Math.Max(0, booked - capacity);

            var state = "non_working";
            if (capacity > 0 || booked > 0)
            {
                if (over > 0)
                {
                    state = "overallocated";
                }
                else if (booked == 0)
                {
                    state = "available";
                }
                else if (available == 0)
                {
                    state = "fully_booked";
                }
                else
                {
                    state = "partially_available";
                }
            }

            return new AvailabilityDay
            {
                Date = d.Date,
                NormalWorkingMinutes = d.NormalWorkingMinutes,
                NormalWorkingHours = ProjectorDateHelpers.MinutesAsHours(d.NormalWorkingMinutes),
                UtilizationBasisMinutes = capacity,
                UtilizationBasisHours = ProjectorDateHelpers.MinutesAsHours(capacity),
                BookedMinutes = booked,
                BookedHours = ProjectorDateHelpers.MinutesAsHours(booked),
                PtoMinutes = pto,
                PtoHours = ProjectorDateHelpers.MinutesAsHours(pto),
                HolidayMinutes = holiday,
                HolidayHours = ProjectorDateHelpers.MinutesAsHours(holiday),
                AvailableMinutes = available,
                AvailableHours = ProjectorDateHelpers.MinutesAsHours(available),
                OverallocatedMinutes = over,
                OverallocatedHours = ProjectorDateHelpers.MinutesAsHours(over),
                State = state
            };
        }).ToList();

        var weeksMap = new Dictionary<string, AvailabilityWeekAccumulator>(StringComparer.Ordinal);
        foreach (var day in days)
        {
            if (string.IsNullOrWhiteSpace(day.Date))
            {
                continue;
            }

            var weekKey = ProjectorDateHelpers.GetWeekStart(day.Date)!;
            if (!weeksMap.TryGetValue(weekKey, out var w))
            {
                w = new AvailabilityWeekAccumulator
                {
                    WeekStart = weekKey,
                    RequiredMinutes = (int)requiredMinutesPerWeek
                };
                weeksMap[weekKey] = w;
            }

            w.UtilizationBasisMinutes += day.UtilizationBasisMinutes;
            w.NormalWorkingMinutes += day.NormalWorkingMinutes;
            w.BookedMinutes += day.BookedMinutes;
            w.PtoMinutes += day.PtoMinutes;
            w.HolidayMinutes += day.HolidayMinutes;
            w.AvailableMinutes += day.AvailableMinutes;
            w.OverallocatedMinutes += day.OverallocatedMinutes;
        }

        var weeks = weeksMap.Values
            .OrderBy(w => w.WeekStart, StringComparer.Ordinal)
            .Select(w =>
            {
                bool? meets = null;
                var shortfall = 0;
                var surplus = 0;
                if (requiredMinutesPerWeek > 0)
                {
                    meets = w.AvailableMinutes >= requiredMinutesPerWeek;
                    if (meets == true)
                    {
                        surplus = w.AvailableMinutes - (int)requiredMinutesPerWeek;
                    }
                    else
                    {
                        shortfall = (int)requiredMinutesPerWeek - w.AvailableMinutes;
                    }
                }

                return new AvailabilityWeek
                {
                    WeekStart = w.WeekStart,
                    UtilizationBasisMinutes = w.UtilizationBasisMinutes,
                    UtilizationBasisHours = ProjectorDateHelpers.MinutesAsHours(w.UtilizationBasisMinutes),
                    NormalWorkingMinutes = w.NormalWorkingMinutes,
                    NormalWorkingHours = ProjectorDateHelpers.MinutesAsHours(w.NormalWorkingMinutes),
                    BookedMinutes = w.BookedMinutes,
                    BookedHours = ProjectorDateHelpers.MinutesAsHours(w.BookedMinutes),
                    PtoMinutes = w.PtoMinutes,
                    PtoHours = ProjectorDateHelpers.MinutesAsHours(w.PtoMinutes),
                    HolidayMinutes = w.HolidayMinutes,
                    HolidayHours = ProjectorDateHelpers.MinutesAsHours(w.HolidayMinutes),
                    AvailableMinutes = w.AvailableMinutes,
                    AvailableHours = ProjectorDateHelpers.MinutesAsHours(w.AvailableMinutes),
                    OverallocatedMinutes = w.OverallocatedMinutes,
                    OverallocatedHours = ProjectorDateHelpers.MinutesAsHours(w.OverallocatedMinutes),
                    RequiredMinutes = w.RequiredMinutes,
                    RequiredHours = ProjectorDateHelpers.MinutesAsHours(w.RequiredMinutes),
                    MeetsRequiredCapacity = meets,
                    ShortfallMinutes = shortfall,
                    ShortfallHours = ProjectorDateHelpers.MinutesAsHours(shortfall),
                    SurplusMinutes = surplus,
                    SurplusHours = ProjectorDateHelpers.MinutesAsHours(surplus)
                };
            }).ToList();

        var overallState = "non_working";
        if (days.Any(d => d.State == "overallocated"))
        {
            overallState = "overallocated";
        }
        else if (days.Any(d => d.State == "fully_booked") && days.All(d => d.AvailableMinutes <= 0))
        {
            overallState = "fully_booked";
        }
        else if (days.Any(d => d.AvailableMinutes > 0) && days.Any(d => d.BookedMinutes > 0))
        {
            overallState = "partially_available";
        }
        else if (days.Any(d => d.AvailableMinutes > 0))
        {
            overallState = "available";
        }

        return new AvailabilitySummary
        {
            ResourceReferenceSystemId = resourceReferenceSystemId,
            DisplayName = displayName,
            EmailAddress = emailAddress,
            State = overallState,
            CapacityBasis = "utilization",
            Days = days,
            Weeks = weeks,
            // Keep Projector-native booking rows (daily + weekly). Expansion is only for capacity math.
            Bookings = schedule.Bookings,
            Roles = schedule.Roles,
            Holidays = schedule.Holidays,
            TimeOff = schedule.TimeOff
        };
    }

    private sealed class AvailabilityWeekAccumulator
    {
        public string? WeekStart { get; init; }
        public int UtilizationBasisMinutes { get; set; }
        public int NormalWorkingMinutes { get; set; }
        public int BookedMinutes { get; set; }
        public int PtoMinutes { get; set; }
        public int HolidayMinutes { get; set; }
        public int AvailableMinutes { get; set; }
        public int OverallocatedMinutes { get; set; }
        public int RequiredMinutes { get; init; }
    }
}
