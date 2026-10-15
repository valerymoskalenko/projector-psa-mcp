using System.Xml.Linq;
using FluentAssertions;
using Projector.ApiClient.Xml;
using Projector.Application.Tools;
using Projector.Domain.Bookings;
using Projector.Domain.Engagements;
using Projector.Domain.Schedule;

namespace Projector.UnitTests;

/// <summary>
/// v0.14.0: weeks read from their Sunday, set_days, comments next to extra_days, task names with other punctuation,
/// weekly bookings spread over the whole week, and per-day rows in list_proj_bookings. Names and codes are invented.
/// </summary>
public class BookingWeekTests
{
    private static Dictionary<string, RoleWeekState> GoLiveWeek() => new(StringComparer.Ordinal)
    {
        ["2026-11-08"] = new RoleWeekState
        {
            WeekStart = "2026-11-08",
            SchedulingMode = "D",
            WeeklyMinutes = 2580,
            DailyMinutes = [0, 720, 720, 240, 450, 450, 0],
            Notes = ["", "Go-live", "Go-live", "", "Migration", "Migration", ""]
        }
    };

    [Fact]
    public void Plan_SetDays_LowersSingleDays_KeepsTheOtherDaysAndNotes()
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", null, null, null, GoLiveWeek(),
            [new BookingPlanner.SetDay("2026-11-09", 8, null), new BookingPlanner.SetDay("2026-11-10", 8, null)]);

        result.Errors.Should().BeEmpty();
        var week = result.Weeks.Should().ContainSingle().Subject;
        week.PreviousMinutes.Should().Be(2580);
        week.DailyMinutes.Should().Equal(0, 480, 480, 240, 450, 450, 0);
        week.NotesChanged.Should().BeFalse("no note was given, so the stored notes are not rewritten");
    }

    [Fact]
    public void Plan_SetDays_ZeroClearsADay_AndItsCommentIsWritten()
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", null, null, null, GoLiveWeek(),
            [new BookingPlanner.SetDay("2026-11-11", 0, "Holiday")]);

        var week = result.Weeks.Should().ContainSingle().Subject;
        week.DailyMinutes.Should().Equal(0, 720, 720, 0, 450, 450, 0);
        week.Notes.Should().Equal("", "Go-live", "Go-live", "Holiday", "Migration", "Migration", "");
    }

    [Fact]
    public void Plan_SetDays_AfterHoursAndExtraDays()
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", 4,
            [new BookingPlanner.ExtraDay("2026-11-12", 3.5, null)], null,
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal),
            [new BookingPlanner.SetDay("2026-11-12", 6, null), new BookingPlanner.SetDay("2026-11-09", 8, null)]);

        result.Weeks.Single().DailyMinutes.Should().Equal(0, 480, 240, 240, 360, 240, 0);
    }

    [Theory]
    [InlineData("2026-11-09", -1, "between 0 and")]
    [InlineData("2026-11-20", 8, "outside start_date..end_date")]
    [InlineData("11/09/2026", 8, "yyyy-MM-dd")]
    public void Plan_SetDays_BadEntries_AreRefused(string date, double hours, string expected)
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", null, null, null, GoLiveWeek(),
            [new BookingPlanner.SetDay(date, hours, null)]);

        result.Weeks.Should().BeEmpty();
        result.Errors.Should().Contain(e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_SetDays_SameDateTwice_IsRefused()
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", null, null, null, GoLiveWeek(),
            [new BookingPlanner.SetDay("2026-11-09", 8, null), new BookingPlanner.SetDay("2026-11-09", 6, null)]);

        result.Errors.Should().ContainSingle(e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_CommentOnTheDateOfAnExtraDayWithoutNote_IsKept()
    {
        // Seen 2026-10-15: comments for the go-live days vanished because the same dates were in extra_days.
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", 4,
            [new BookingPlanner.ExtraDay("2026-11-09", 4, null)],
            [new BookingPlanner.CommentDay("2026-11-09", "Go-live")],
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        var week = result.Weeks.Should().ContainSingle().Subject;
        week.NotesChanged.Should().BeTrue();
        week.Notes![1].Should().Be("Go-live");
    }

    [Fact]
    public void Plan_ExtraDayNote_StillWinsOverComment()
    {
        var result = BookingPlanner.Plan(
            "2026-11-08", "2026-11-14", "daily", 4,
            [new BookingPlanner.ExtraDay("2026-11-09", 4, "From extra day")],
            [new BookingPlanner.CommentDay("2026-11-09", "From comments")],
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        result.Weeks.Single().Notes![1].Should().Be("From extra day");
    }

    [Fact]
    public void Plan_CurrentWeeksNotKeyedBySunday_IsRefused_NothingPlanned()
    {
        var current = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal)
        {
            ["2026-11-12"] = new RoleWeekState { WeekStart = "2026-11-12", SchedulingMode = "W", WeeklyMinutes = 1200 }
        };

        var result = BookingPlanner.Plan(
            "2026-11-12", "2026-11-13", "daily", null,
            [new BookingPlanner.ExtraDay("2026-11-12", 3.5, null)], null, current);

        result.Weeks.Should().BeEmpty();
        result.Errors.Should().ContainSingle(e => e.StartsWith("week_alignment", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTask_IgnoresPunctuationAndCase_AndNotFoundSuggestsClosest()
    {
        var plan = new ProjectTaskPlan
        {
            Tasks =
            [
                new ProjectPlanTask { TaskUid = "1", WbsCode = "30", TaskName = "Vendor: License Audit" },
                new ProjectPlanTask { TaskUid = "2", WbsCode = "31", TaskName = "Northwind: User Migration" },
                new ProjectPlanTask { TaskUid = "3", WbsCode = "12", TaskName = "SysAdmin: User Onboarding" }
            ]
        };

        BookingToolService.FindTask(plan, "northwind user migration")!.WbsCode.Should().Be("31");
        BookingToolService.FindTask(plan, "Northwind - User Migration")!.WbsCode.Should().Be("31");
        BookingToolService.FindTask(plan, "Contoso migration").Should().BeNull();
        BookingToolService.ClosestTasks(plan, "Contoso user migration")
            .Should().StartWith("31 Northwind: User Migration");
    }

    [Fact]
    public void Schedule_WeeklyBooking_IsSpreadOverTheWholeWeek_ForAWindowOfTwoDays()
    {
        // Seen 2026-10-15: 40 h weekly on another project showed as 20 h a day for a Mon–Tue window.
        var full = new ResourceSchedule
        {
            Dates = WeekDates("2026-11-08", 480),
            Bookings =
            [
                Weekly("P900001-001", "2026-11-08", 2400),
                Daily("P900002-001", "2026-11-09", 480),
                Daily("P900002-001", "2026-11-10", 480),
                Daily("P900002-001", "2026-11-12", 240)
            ],
            Holidays = [new ScheduleHoliday { Date = "2026-11-12", HolidayName = "Holiday", TimeOffMinutes = 480 }]
        };

        var trimmed = AvailabilityCalculator.TrimToWindow(full, "2026-11-09", "2026-11-10");
        var summary = AvailabilityCalculator.ToAvailabilitySummary(trimmed);

        trimmed.Dates.Select(d => d.Date).Should().Equal("2026-11-09", "2026-11-10");
        trimmed.Holidays.Should().BeEmpty("the holiday is outside the window");
        trimmed.Bookings.Should().HaveCount(3, "the weekly row and the two daily rows inside the window");
        summary.Days.Select(d => d.BookedMinutes).Should().Equal(960, 960);
        summary.Days.Should().AllSatisfy(d => d.OverallocatedMinutes.Should().Be(480));
    }

    [Fact]
    public void Schedule_WeeklyBooking_InAWeekWithoutCapacity_CountsAsOverallocated()
    {
        // Seen 2026-10-15: a person with a 0-hour work calendar showed nothing booked; Projector shows -20.
        var full = new ResourceSchedule
        {
            Dates = WeekDates("2026-11-01", 0),
            Bookings = [Weekly("P900001-001", "2026-11-01", 1200)]
        };

        var summary = AvailabilityCalculator.ToAvailabilitySummary(
            AvailabilityCalculator.TrimToWindow(full, "2026-11-01", "2026-11-07"));

        summary.Weeks.Single().BookedMinutes.Should().Be(1200);
        summary.Weeks.Single().OverallocatedMinutes.Should().Be(1200);
        summary.State.Should().Be("overallocated");
    }

    [Fact]
    public void ProjectBookings_WindowStartingMidWeek_KeepsThatWeek_WithDailyMinutes()
    {
        var response = XDocument.Parse(
            """
            <r>
              <PwsProjectRoleSchedule>
                <ProjectCode>P900001-001</ProjectCode>
                <ProjectRoleName>Engineer</ProjectRoleName>
                <BookedBuckets>
                  <PwsProjectRoleHoursBucket>
                    <BucketStartDate>2026-11-08T00:00:00Z</BucketStartDate>
                    <SchedulingMode>D</SchedulingMode>
                    <WeeklyMinutes>2100</WeeklyMinutes>
                    <DailyMinutes><i>0</i><i>480</i><i>480</i><i>240</i><i>450</i><i>450</i><i>0</i></DailyMinutes>
                  </PwsProjectRoleHoursBucket>
                  <PwsProjectRoleHoursBucket>
                    <BucketStartDate>2026-11-15T00:00:00Z</BucketStartDate>
                    <SchedulingMode>W</SchedulingMode>
                    <WeeklyMinutes>1200</WeeklyMinutes>
                  </PwsProjectRoleHoursBucket>
                </BookedBuckets>
              </PwsProjectRoleSchedule>
            </r>
            """);

        var rows = ProjectorResponseParsers.ParseResourceSchedulingRoleData(response, "2026-11-12", "2026-11-13");

        var row = rows.Should().ContainSingle().Subject;
        row.Date.Should().Be("2026-11-08");
        row.ScheduledMinutes.Should().Be(2100);
        row.DailyMinutes.Should().Equal(0, 480, 480, 240, 450, 450, 0);
    }

    [Theory]
    [InlineData("5764000000000000001", "ResourceUid")]
    [InlineData("10001", "ResourceReferenceSystemId")]
    public void ScheduleEnvelope_SendsAUidForPeopleWithoutEmployeeId(string id, string element)
    {
        var xml = XDocument.Parse(ProjectorEnvelopeBuilders.BuildGetResourceSchedule("ticket", id, "2026-11-08", "2026-11-14"));

        var identity = xml.Descendants().Single(e => e.Name.LocalName == "ResourceIdentity");
        identity.Elements().Single().Name.LocalName.Should().Be(element);
        identity.Value.Should().Be(id);
    }

    private static List<ScheduleDate> WeekDates(string sunday, int minutesOnWeekdays)
    {
        var start = DateTime.ParseExact(sunday, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Enumerable.Range(0, 7).Select(i =>
        {
            var day = start.AddDays(i);
            var weekday = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            return new ScheduleDate
            {
                Date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                NormalWorkingMinutes = weekday ? minutesOnWeekdays : 0,
                UtilizationBasisMinutes = weekday ? minutesOnWeekdays : 0
            };
        }).ToList();
    }

    private static ScheduleBooking Weekly(string project, string sunday, int minutes) =>
        new() { ProjectCode = project, DailyWeeklyFlag = "W", Date = sunday, ScheduledMinutes = minutes };

    private static ScheduleBooking Daily(string project, string date, int minutes) =>
        new() { ProjectCode = project, DailyWeeklyFlag = "D", Date = date, ScheduledMinutes = minutes };
}
