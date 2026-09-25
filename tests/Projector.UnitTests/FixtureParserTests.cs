using System.Xml.Linq;
using FluentAssertions;
using Projector.ApiClient.Xml;
using Projector.Domain.Schedule;

namespace Projector.UnitTests;

/// <summary>
/// Parser tests against captured Projector SOAP fixtures (not invented mock tenants).
/// </summary>
public class FixtureParserTests
{
    private static readonly string FixturesDir = ResolveFixturesDir();

    private static string ResolveFixturesDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "tests", "Projector.UnitTests", "fixtures"))
        };

        return candidates.FirstOrDefault(Directory.Exists)
            ?? throw new DirectoryNotFoundException(
                "Could not locate tests/Projector.UnitTests/fixtures.");
    }

    private static XDocument ReadFixture(string name) =>
        XDocument.Load(Path.Combine(FixturesDir, name));

    [Fact]
    public void ParseResourceList_ReturnsTwoResources()
    {
        var resources = ProjectorResponseParsers.ParseResourceList(ReadFixture("resource_list.xml"));
        resources.Should().HaveCount(2);
        resources[0].ResourceReferenceSystemId.Should().Be("10101");
    }

    [Fact]
    public void ParseResource_IncludesHistoryAndUdfs()
    {
        var resource = ProjectorResponseParsers.ParseResource(ReadFixture("resource_detail.xml"), true, true);
        resource.Should().NotBeNull();
        resource!.DisplayName.Should().Be("Alice Example");
        resource.LocationName.Should().Be("US - United States");
        resource.Udfs.Should().HaveCount(2);
        resource.History.Should().HaveCount(1);
    }

    [Fact]
    public void ParseTimeCards_FiltersAndMapsFields()
    {
        var all = ProjectorResponseParsers.ParseTimeCards(ReadFixture("timecards.xml"));
        all.Should().HaveCount(3);
        all[0].WorkHours.Should().Be(8);
        all[0].EngagementCode.Should().Be("E005678");
        all[0].CardStatusCode.Should().Be("A");

        var approved = ProjectorResponseParsers.ParseTimeCards(ReadFixture("timecards.xml"), status: "Approved");
        approved.Should().HaveCount(2);

        var byProject = ProjectorResponseParsers.ParseTimeCards(
            ReadFixture("timecards.xml"), projectCode: "P005678-001");
        byProject.Should().HaveCount(2);
    }

    [Fact]
    public void ParseTimeOff_MapsReasonAndStatus()
    {
        var cards = ProjectorResponseParsers.ParseTimeOff(ReadFixture("time_off.xml"));
        cards.Should().HaveCount(2);
        cards[0].TimeOffReason.Should().Be("PTO");
        cards[0].TimeOffMinutes.Should().Be(480);
        cards[0].TimeOffHours.Should().Be(8);
    }

    [Fact]
    public void ParseResourceSchedule_KeepsNativeBookings()
    {
        var schedule = ProjectorResponseParsers.ParseResourceSchedule(ReadFixture("resource_schedule.xml"));
        schedule.Bookings.Should().NotBeEmpty();
        schedule.Bookings.Should().Contain(b =>
            string.Equals(b.DailyWeeklyFlag, "D", StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.DailyWeeklyFlag, "W", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(b.SchedulingMode));

        var summary = AvailabilityCalculator.ToAvailabilitySummary(schedule, "10101", requiredMinutesPerWeek: 2400);
        // Capacity math may explode weekly rows, but availability.bookings stays Projector-native.
        summary.Bookings.Should().BeEquivalentTo(schedule.Bookings);
        summary.Bookings.Should().AllSatisfy(b =>
        {
            b.Should().BeOfType<ScheduleBooking>();
            (b.ScheduledMinutes > 0 || b.Date is not null).Should().BeTrue();
        });
        summary.Days.Should().NotBeEmpty();
        var weeklyOnly = summary.WithoutDays();
        weeklyOnly.Days.Should().BeNull();
        weeklyOnly.Weeks.Should().BeEquivalentTo(summary.Weeks);
    }

    [Fact]
    public void ParseEngagementList_AndDetail()
    {
        var list = ProjectorResponseParsers.ParseEngagementList(ReadFixture("engagement_list.xml"));
        list.Should().NotBeEmpty();

        var detail = ProjectorResponseParsers.ParseEngagement(ReadFixture("engagement_detail.xml"));
        detail.Should().NotBeNull();
        detail!.EngagementCode.Should().NotBeNullOrWhiteSpace();
        detail.BudgetVisibility.Should().Be("hours_only");
        detail.WorkMinutesTimeBudgetAmount.Should().Be(4800);
        detail.WorkHoursTimeBudgetAmount.Should().Be(80);
        detail.ChargeableMinutesTimeBudgetAmount.Should().Be(3600);
        detail.ChargeableHoursTimeBudgetAmount.Should().Be(60);
        detail.ContractRevenueTimeBudgetAmount.Should().BeNull();
        detail.CurrencyCode.Should().BeNull();
    }

    [Fact]
    public void ParseEngagementDetail_WithMoneyBudgets()
    {
        var detail = ProjectorResponseParsers.ParseEngagement(ReadFixture("engagement_detail_with_money.xml"));
        detail.Should().NotBeNull();
        detail!.BudgetVisibility.Should().Be("hours_and_money");
        detail.WorkMinutesTimeBudgetAmount.Should().Be(4800);
        detail.WorkHoursTimeBudgetAmount.Should().Be(80);
        detail.CurrencyCode.Should().Be("USD");
        detail.TimeBudgetMetric.Should().Be("C");
        detail.TimeBudgetMetricLabel.Should().Be("Contract Revenue");
        detail.ContractRevenueTimeBudgetAmount.Should().Be(50000);
        detail.BillingAdjustedRevenueTimeBudgetAmount.Should().Be(48000);
        detail.ResourceDirectCostTimeBudgetAmount.Should().Be(30000);
        detail.CostBudgetMetric.Should().Be("C");
        detail.CostBudgetMetricLabel.Should().Be("Client Amount");
        detail.ClientAmountCostBudgetAmount.Should().Be(10000);
        detail.ProjectContractRevenueTimeBudgetAmount.Should().Be(75000);
        detail.ProjectWorkMinutesTimeBudgetAmount.Should().Be(4800);
    }

    [Fact]
    public void IsRowCountExceeded_DetectsTruncationFixture()
    {
        ProjectorSoapHttp.IsRowCountExceeded(ReadFixture("engagement_list.xml")).Should().BeFalse();
        ProjectorSoapHttp.IsRowCountExceeded(ReadFixture("engagement_list_truncated.xml")).Should().BeTrue();
    }

    [Fact]
    public void ExpandWeeklyBookings_EmptyBookingsOk()
    {
        var schedule = ProjectorResponseParsers.ParseResourceSchedule(ReadFixture("resource_schedule.xml"));
        var daily = AvailabilityCalculator.ExpandWeeklyBookings([], schedule.Dates);
        daily.Should().BeEmpty();
    }

    [Fact]
    public void ParseProjectRoles_ReturnsRosterRows()
    {
        var roles = ProjectorResponseParsers.ParseProjectRoles(ReadFixture("project_roles.xml"));
        roles.Should().HaveCount(2);
        roles[0].ProjectCode.Should().Be("P001234-001");
        roles[0].DisplayName.Should().Be("Dan Miller");
        roles[0].ResourceId.Should().Be("10280");
        roles[0].RoleName.Should().Be("Application Specialist - Team 1");
        roles[1].DisplayName.Should().Be("Carol Jones");
        roles[1].RoleName.Should().Be("PMO Oversight");
    }

    [Fact]
    public void ParseProjectBookings_FiltersWindowAndDropsZeroHours()
    {
        var rows = ProjectorResponseParsers.ParseResourceSchedulingRoleData(
            ReadFixture("project_bookings.xml"), "2026-10-01", "2026-10-31");
        rows.Should().HaveCount(2);

        var jane = rows.Single(r => r.DisplayName == "Jane Doe");
        jane.ScheduledMinutes.Should().Be(720);
        jane.ScheduledHours.Should().Be(12);
        jane.Email.Should().Be("jane.doe@example.com");
        jane.Date.Should().Be("2026-10-04");
        jane.SchedulingMode.Should().Be("weekly");

        var carol = rows.Single(r => r.DisplayName == "Carol Jones");
        carol.ScheduledMinutes.Should().Be(480);
        carol.SchedulingMode.Should().Be("daily");

        rows.Should().NotContain(r => r.Date == "2026-11-01");
    }

    [Fact]
    public void BuildSchedulingRoleDataEnvelope_UsesDirectProjectCode()
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetResourceSchedulingRoleData(
            "TICKET", "P001234-001", "2026-10-01", "A", 5);
        xml.Should().Contain("PwsGetResourceSchedulingRoleData");
        xml.Should().Contain("<sch:RequestOrScheduleMode>A</sch:RequestOrScheduleMode>");
        xml.Should().Contain("<com:ProjectCode>P001234-001</com:ProjectCode>");
        xml.Should().NotContain("PwsProjectRef");
        xml.Should().NotContain("ProjectRoleIdentities");
        xml.Should().Contain("MinimumWeekCount>5");
        xml.Should().Contain("2026-10-01T00:00:00.000Z");

        ProjectorDateHelpers.GetMinimumWeekCountForWindow("2026-10-01", "2026-10-31").Should().Be(5);
        ProjectorDateHelpers.GetMinimumWeekCountForWindow("2026-10-01", "2026-10-07").Should().Be(1);
    }

    [Fact]
    public void BuildGetProjectRolesEnvelope_UsesProjectRefsAndModeA()
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetProjectRoles(
            "TICKET", ["P001234-001", "P001942-001"]);
        xml.Should().Contain("PwsGetProjectRoles");
        xml.Should().Contain("<sch:Mode>A</sch:Mode>");
        xml.Should().Contain("PwsProjectRef");
        xml.Should().Contain("P001234-001");
        xml.Should().Contain("IncludeDeletedRolesFlag>false");
    }
}
