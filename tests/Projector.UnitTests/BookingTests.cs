using System.Xml.Linq;
using FluentAssertions;
using Projector.ApiClient.Xml;
using Projector.Application.Tools;
using Projector.Domain.Bookings;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Tools;

namespace Projector.UnitTests;

/// <summary>
/// save_booking: planner (per-week hours, extra days, note merge), envelopes (no submit/finalize),
/// and Copilot stripped name uniqueness. Live writes are manual, never CI.
/// </summary>
public class BookingTests
{
    private static readonly string SrcDir = ResolveDir("src"); // server/src when walking up from the test binary

    [Fact]
    public void Plan_Weekly_SetsHoursOnEachIncludedWeek_AndSkipsPartialEndWeek()
    {
        // 4 Oct 2026 (Sun) through 28 Nov: eight full weeks; week of 29 Nov runs into December.
        var result = BookingPlanner.Plan(
            "2026-10-04",
            "2026-11-28",
            "weekly",
            hoursPerPeriod: 20,
            extraDays: null,
            comments: null,
            currentByWeek: new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        result.Errors.Should().BeEmpty();
        result.Weeks.Should().HaveCount(8);
        result.Weeks.Select(w => w.WeekStart).Should().Equal(
            "2026-10-04", "2026-10-11", "2026-10-18", "2026-10-25",
            "2026-11-01", "2026-11-08", "2026-11-15", "2026-11-22");
        result.Weeks.Should().AllSatisfy(w =>
        {
            w.SchedulingMode.Should().Be("W");
            w.WeeklyMinutes.Should().Be(20 * 60);
            w.NewMinutes.Should().Be(20 * 60);
        });
        result.Weeks.Select(w => w.WeekStart).Should().NotContain("2026-11-29");

        // When end_date falls inside the next week, that week is named as skipped (Saturday past end).
        var withPartial = BookingPlanner.Plan(
            "2026-10-04",
            "2026-11-30",
            "weekly",
            20,
            null,
            null,
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));
        withPartial.Weeks.Should().HaveCount(8);
        withPartial.SkippedWeeks.Should().ContainSingle(s => s.StartsWith("2026-11-29", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ExtraDays_TurnWeekIntoDaily_SpreadThenAdd()
    {
        // Week of 1 Nov 2026: 20 h weekly + 8 h Mon 2 Nov + 8 h Tue 3 Nov → Mon 12, Tue 12, Wed–Fri 4.
        var extras = new[]
        {
            new BookingPlanner.ExtraDay("2026-11-02", 8, "Go-Live A"),
            new BookingPlanner.ExtraDay("2026-11-03", 8, "Go-Live A")
        };
        var result = BookingPlanner.Plan(
            "2026-11-01",
            "2026-11-07",
            "weekly",
            20,
            extras,
            comments: null,
            currentByWeek: new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        result.Errors.Should().BeEmpty();
        var week = result.Weeks.Should().ContainSingle().Subject;
        week.WeekStart.Should().Be("2026-11-01");
        week.SchedulingMode.Should().Be("D");
        week.DailyMinutes.Should().Equal(
            0,           // Sun
            12 * 60,     // Mon 4+8
            12 * 60,     // Tue 4+8
            4 * 60,      // Wed
            4 * 60,      // Thu
            4 * 60,      // Fri
            0);          // Sat
        week.NewMinutes.Should().Be(36 * 60);
        week.Notes.Should().NotBeNull();
        week.Notes![1].Should().Be("Go-Live A");
        week.Notes[2].Should().Be("Go-Live A");
    }

    [Fact]
    public void Plan_NoteMerge_KeepsUnmentionedDays_EmptyClears()
    {
        var current = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal)
        {
            ["2026-11-01"] = new RoleWeekState
            {
                WeekStart = "2026-11-01",
                SchedulingMode = "W",
                WeeklyMinutes = 600,
                Notes = ["keep-sun", "old-mon", "keep-tue", "", "", "", "keep-sat"]
            }
        };

        var result = BookingPlanner.Plan(
            "2026-11-01",
            "2026-11-07",
            "weekly",
            20,
            extraDays: null,
            comments:
            [
                new BookingPlanner.CommentDay("2026-11-02", "new-mon"),
                new BookingPlanner.CommentDay("2026-11-03", "")
            ],
            current);

        var week = result.Weeks.Should().ContainSingle().Subject;
        week.NotesChanged.Should().BeTrue();
        week.Notes.Should().Equal(
            "keep-sun", "new-mon", "", "", "", "", "keep-sat");
        week.SchedulingMode.Should().Be("W");
        week.WeeklyMinutes.Should().Be(20 * 60);
    }

    [Fact]
    public void Plan_WithoutHours_KeepsCurrentWeekAndAddsExtras_OnlyTouchedWeeksWritten()
    {
        // 20 h stored weekly on 1 Nov and 8 Nov; "add 8 h on 9 Nov" without hours keeps the 20 h.
        var current = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal)
        {
            ["2026-11-01"] = new RoleWeekState { WeekStart = "2026-11-01", SchedulingMode = "W", WeeklyMinutes = 1200 },
            ["2026-11-08"] = new RoleWeekState { WeekStart = "2026-11-08", SchedulingMode = "W", WeeklyMinutes = 1200 }
        };

        var result = BookingPlanner.Plan(
            "2026-11-01",
            "2026-11-14",
            "weekly",
            hoursPerPeriod: null,
            extraDays: [new BookingPlanner.ExtraDay("2026-11-09", 8, "Go-Live")],
            comments: null,
            current);

        result.Errors.Should().BeEmpty();
        var week = result.Weeks.Should().ContainSingle("the week of 1 Nov is not touched, so it is not written").Subject;
        week.WeekStart.Should().Be("2026-11-08");
        week.SchedulingMode.Should().Be("D");
        week.DailyMinutes.Should().Equal(0, 4 * 60 + 8 * 60, 4 * 60, 4 * 60, 4 * 60, 4 * 60, 0);
        week.PreviousMinutes.Should().Be(1200);
        week.NewMinutes.Should().Be(28 * 60);
    }

    [Fact]
    public void Plan_WithoutHours_KeepsStoredDailyAmounts()
    {
        var current = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal)
        {
            ["2026-11-01"] = new RoleWeekState
            {
                WeekStart = "2026-11-01",
                SchedulingMode = "D",
                WeeklyMinutes = 36 * 60,
                DailyMinutes = [0, 720, 720, 240, 240, 240, 0]
            }
        };

        var result = BookingPlanner.Plan(
            "2026-11-01", "2026-11-07", "weekly", null,
            [new BookingPlanner.ExtraDay("2026-11-06", 2, null)], null, current);

        result.Errors.Should().BeEmpty();
        result.Weeks.Single().DailyMinutes.Should().Equal(0, 720, 720, 240, 240, 360, 0);
    }

    [Fact]
    public void Plan_WithoutHours_CommentsOnly_KeepHoursAsStored()
    {
        var current = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal)
        {
            ["2026-11-01"] = new RoleWeekState { WeekStart = "2026-11-01", SchedulingMode = "W", WeeklyMinutes = 1200 }
        };

        var result = BookingPlanner.Plan(
            "2026-11-01", "2026-11-07", "weekly", null, null,
            [new BookingPlanner.CommentDay("2026-11-02", "kick-off")], current);

        var week = result.Weeks.Should().ContainSingle().Subject;
        week.SchedulingMode.Should().Be("W");
        week.WeeklyMinutes.Should().Be(1200);
        week.NewMinutes.Should().Be(1200);
        week.Notes![1].Should().Be("kick-off");
    }

    [Fact]
    public void Plan_WithoutHoursExtrasOrComments_IsRefused()
    {
        var result = BookingPlanner.Plan(
            "2026-11-01", "2026-11-07", "weekly", null, null, null,
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        result.Weeks.Should().BeEmpty();
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Nothing to change");
    }

    [Fact]
    public void Plan_ExtraDayOutsideRangeOrInSkippedWeek_IsRefused_NotDropped()
    {
        var none = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal);

        // Outside start_date..end_date.
        var outside = BookingPlanner.Plan(
            "2026-11-01", "2026-11-07", "weekly", 20,
            [new BookingPlanner.ExtraDay("2026-11-09", 8, null)], null, none);
        outside.Weeks.Should().BeEmpty();
        outside.Errors.Should().ContainSingle().Which.Should().Contain("2026-11-09").And.Contain("outside");

        // Inside the range, but in the last week, which weekly mode skips because it runs past end_date.
        var skipped = BookingPlanner.Plan(
            "2026-11-01", "2026-11-10", "weekly", 20,
            [new BookingPlanner.ExtraDay("2026-11-09", 8, null)], null, none);
        skipped.Weeks.Should().BeEmpty();
        skipped.Errors.Should().ContainSingle().Which.Should().Contain("2026-11-09").And.Contain("runs past end_date");

        // A comment outside the range is refused too.
        var comment = BookingPlanner.Plan(
            "2026-11-01", "2026-11-07", "weekly", 20, null,
            [new BookingPlanner.CommentDay("2026-10-30", "x")], none);
        comment.Errors.Should().ContainSingle().Which.Should().Contain("comments date 2026-10-30");
    }

    [Fact]
    public void SkippedWeek_IsExplainedInPlainWords()
    {
        var result = BookingPlanner.Plan(
            "2026-11-22", "2026-11-30", "weekly", 20, null, null,
            new Dictionary<string, RoleWeekState>(StringComparer.Ordinal));

        result.SkippedWeeks.Should().ContainSingle().Which.Should()
            .Be("2026-11-29..2026-12-05 not booked: the week runs past end_date 2026-11-30 " +
                "(weekly mode books full Sunday–Saturday weeks; move end_date to the Saturday or use daily)");
    }

    [Fact]
    public void LockFault_MapsToProjectLocked_AndSaysWhatWasWritten()
    {
        var fault = new Projector.Domain.Exceptions.ProjectorApiException(
            "One or more existing locks prevent acquisition of requested lock.", "EntityAlreadyLocked");

        var nothing = BookingToolService.MapSaveError(fault, createdRoleUid: null, assignedTask: false);
        nothing.ErrorCode.Should().Be(BookingToolService.ProjectLocked);
        nothing.Message.Should().Contain("Edit mode").And.Contain("Nothing was booked");

        var withRole = BookingToolService.MapSaveError(fault, createdRoleUid: "123", assignedTask: true);
        withRole.Message.Should().Contain("role_uid 123").And.Contain("assigned to the task")
            .And.Contain("no hours were booked");
    }

    [Fact]
    public async Task SaveGate_OneSavePerProject_SecondWaitsOrTimesOut()
    {
        var gate = new ProjectSaveGate();
        var first = await gate.TryEnterAsync("C000123-001", TimeSpan.FromSeconds(1), CancellationToken.None);
        first.Should().NotBeNull();

        // Same project (any case) is busy; another project is free.
        (await gate.TryEnterAsync("c000123-001", TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .Should().BeNull();
        using (var other = await gate.TryEnterAsync("C000001-001", TimeSpan.FromMilliseconds(50), CancellationToken.None))
        {
            other.Should().NotBeNull();
        }

        var waiting = gate.TryEnterAsync("C000123-001", TimeSpan.FromSeconds(5), CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();
        first!.Dispose();
        using (var second = await waiting)
        {
            second.Should().NotBeNull();
        }

        gate.ActiveProjects.Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(BookingTools), nameof(BookingTools.SaveBooking))]
    [InlineData(typeof(TimeEntryTools), nameof(TimeEntryTools.SaveTimecard))]
    [InlineData(typeof(ExpenseTools), nameof(ExpenseTools.SaveExpenses))]
    public void WriteTools_DefaultToDryRun(Type type, string method)
    {
        var dryRun = type.GetMethod(method)!.GetParameters().Single(p => p.Name == "dry_run");
        dryRun.HasDefaultValue.Should().BeTrue();
        dryRun.DefaultValue.Should().Be(true, "a save needs an explicit dry_run = false");
    }

    [Fact]
    public void BookEnvelope_HasNoSubmitFinalizeOrClearFlag()
    {
        var body = ProjectorBookingEnvelopes.RequestOrBookRoleHours("ticket", new BookRoleHoursRequest
        {
            RoleUid = "9900000000000000001",
            HoursBuckets =
            [
                new RoleHoursBucket
                {
                    WeekStart = "2026-10-04",
                    SchedulingMode = "W",
                    WeeklyMinutes = 1200
                },
                new RoleHoursBucket
                {
                    WeekStart = "2026-11-01",
                    SchedulingMode = "D",
                    DailyMinutes = [0, 720, 720, 240, 240, 240, 0]
                }
            ],
            NotesBuckets =
            [
                new RoleHoursBucket
                {
                    WeekStart = "2026-11-01",
                    SchedulingMode = "D",
                    Notes = ["", "Go-Live A", "Go-Live A", "", "", "", ""]
                }
            ]
        });

        var xml = body.ToString(SaveOptions.DisableFormatting);
        var doc = new XDocument(body);
        doc.Descendants().First(e => e.Name.LocalName == "Mode").Value.Should().Be("A");
        doc.Descendants().Count(e => e.Name.LocalName == "BucketStartDate").Should().Be(3);
        doc.Descendants().First(e => e.Name.LocalName == "WeeklyMinutes").Value.Should().Be("1200");
        doc.Descendants().Count(e => e.Name.LocalName == "short" && e.Value == "720").Should().Be(2);
        xml.Should().Contain("Go-Live A");
        xml.Should().NotContain("SubmitOrder");
        xml.Should().NotContain("FinalizeOrder");
        xml.Should().NotContain("ClearExistingHoursFlag");
        xml.Should().NotContain("CandidateResourceIdentity");
        xml.Should().NotContain("Timestamp");

        // Array item types are Common (WCF ignores Scheduling-namespaced items → silent Ok, no hours).
        doc.Descendants().First(e => e.Name.LocalName == "PwsProjectRoleHours").Name.NamespaceName
            .Should().Be("http://projectorpsa.com/DataContracts/Shared/Common/");
        doc.Descendants().First(e => e.Name.LocalName == "PwsProjectRoleHoursBucket").Name.NamespaceName
            .Should().Be("http://projectorpsa.com/DataContracts/Shared/Common/");

        // Hours and notes before ProjectRoleIdentity (documented order).
        var hoursIdx = xml.IndexOf("HoursBuckets", StringComparison.Ordinal);
        var notesIdx = xml.IndexOf("NotesBuckets", StringComparison.Ordinal);
        var roleIdx = xml.IndexOf("ProjectRoleIdentity", StringComparison.Ordinal);
        hoursIdx.Should().BeGreaterThan(0);
        notesIdx.Should().BeGreaterThan(hoursIdx);
        roleIdx.Should().BeGreaterThan(notesIdx);
    }

    [Fact]
    public void SaveProjectRoleEnvelope_ModeA_AnyCriteria_NoUniqueRename()
    {
        var body = ProjectorBookingEnvelopes.SaveProjectRole("ticket", new SaveProjectRoleRequest
        {
            ProjectCode = "C000123-001",
            RoleName = "Jane Doe",
            ResourceUid = "9900000000000000002",
            DefaultSchedulingMode = "W"
        });
        var xml = body.ToString(SaveOptions.DisableFormatting);

        body.Descendants().First(e => e.Name.LocalName == "Mode").Value.Should().Be("A");
        body.Descendants().First(e => e.Name.LocalName == "RoleName").Value.Should().Be("Jane Doe");
        body.Descendants().First(e => e.Name.LocalName == "CostCenterCriteriaClearFlag").Value.Should().Be("true");
        body.Descendants().First(e => e.Name.LocalName == "LocationCriteriaClearFlag").Value.Should().Be("true");
        body.Descendants().First(e => e.Name.LocalName == "ResourceTypeCriteriaClearFlag").Value.Should().Be("true");
        body.Descendants().Any(e => e.Name.LocalName == "ResourceTypeAnyFlag").Should().BeFalse();
        body.Descendants().First(e => e.Name.LocalName == "MakeRoleNameUniqueFlag").Value.Should().Be("false");
        body.Descendants().First(e => e.Name.LocalName == "ResourceUid").Value.Should().Be("9900000000000000002");
        xml.Should().Contain("C000123-001");
    }

    [Fact]
    public void Source_NeverUsesSubmitOrFinalizeOrder()
    {
        var forbidden = new[] { "SubmitOrder", "FinalizeOrder", "ClearExistingHoursFlag" };
        var offenders = Directory.EnumerateFiles(SrcDir, "*Booking*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token)).Select(t => $"{f}: {t}"))
            .ToList();
        offenders.Should().BeEmpty(
            "booking source may mention the tokens only in comments that say they are never used — " +
            "if this fails, keep comments without the exact identifiers or move them out of *Booking*.cs");
    }

    [Fact]
    public void CopilotStrippedName_Booking_IsUnique()
    {
        CopilotToolNameFilter.Resolve("booking", ToolCatalog.CanonicalAgentTools).Should().Be("save_booking");
        ToolCatalog.CanonicalAgentTools.Should().Contain("save_booking");
        var stripped = ToolCatalog.CanonicalAgentTools
            .Select(n => n[(n.IndexOf('_') + 1)..])
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        // expenses is the known list/save pair; booking must not join another collision.
        stripped.Should().NotContain("booking");
        stripped.Where(s => !string.Equals(s, "expenses", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    private static string ResolveDir(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(string.Join('/', relative));
    }
}
