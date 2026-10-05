using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Projector.Application.Auth;
using Projector.Application.Tools;
using Projector.Domain.Auth;
using Projector.Domain.Resources;
using Projector.Domain.Timecards;

namespace Projector.UnitTests;

/// <summary>
/// Person-shaped tools: no resource (or "me") = the signed-in user, a numeric id is used as is,
/// a name or e-mail is looked up first.
/// </summary>
public class ResourceArgumentTests
{
    private const string ConnectionId = "test";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("me")]
    [InlineData(" ME ")]
    public async Task ListTimecards_WithoutPerson_AsksProjectorForTheCaller(string? resource)
    {
        var (service, soap) = Create();

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, resource, "2026-09-25", "2026-09-25", null, null, CancellationToken.None));

        soap.Calls.Should().Equal("ListTimecardsAsync(<none>)");
        result.GetProperty("resource_id").GetString().Should().Be("me");
    }

    [Fact]
    public async Task ListTimecards_NumericId_IsUsedWithoutALookup()
    {
        var (service, soap) = Create();

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, "10001", "2026-09-25", "2026-09-25", null, null, CancellationToken.None));

        soap.Calls.Should().Equal("ListTimecardsAsync(10001)");
        result.GetProperty("resource_id").GetString().Should().Be("10001");
    }

    [Fact]
    public async Task ListTimecards_Name_IsResolvedToTheResourceId()
    {
        var (service, soap) = Create();
        soap.ResourcesByName["Jane Doe"] = new ResourceDetail { ResourceReferenceSystemId = "10001", DisplayName = "Jane Doe" };

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, "Jane Doe", "2026-09-25", "2026-09-25", null, null, CancellationToken.None));

        soap.Calls.Should().EndWith("ListTimecardsAsync(10001)");
        result.GetProperty("resource_id").GetString().Should().Be("10001");
    }

    [Fact]
    public async Task ListTimecards_OwnCards_AreMarkedEditableOnlyWhenDraftOrRejected()
    {
        var (service, soap) = Create();
        soap.Timecards.AddRange([Card("1", "D"), Card("2", "R"), Card("3", "S"), Card("4", "A")]);

        var mine = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-25", "2026-09-25", null, null, CancellationToken.None));
        mine.GetProperty("timecards").EnumerateArray().Select(c => c.GetProperty("Editable").GetBoolean())
            .Should().Equal(true, true, false, false);

        var someoneElse = Json(await service.ListTimecardsAsync(
            ConnectionId, "10001", "2026-09-25", "2026-09-25", null, null, CancellationToken.None));
        someoneElse.GetProperty("timecards").EnumerateArray()
            .Should().AllSatisfy(c => c.TryGetProperty("Editable", out _).Should().BeFalse("only the user's own cards say whether save_timecard can change them"));
    }

    [Fact]
    public async Task ListTimecards_Compact_ReturnsShortCards()
    {
        var (service, soap) = Create();
        soap.Timecards.Add(new Timecard
        {
            TimecardUid = "1",
            CardStatusCode = "D",
            Description = "Payroll checkpoint",
            ProjectCode = "P005678-001",
            ProjectName = "Operations",
            ClientName = "Contoso",
            EngagementName = "Internal",
            TaskName = "Staff Management",
            TaskPath = "Operations > Staff Management",
            TaskWbsCode = "2.1",
            RoleName = "Consultant",
            RateTypeName = "Billable",
            ProjectTaskUid = "task-uid",
            WorkDate = "2026-09-25",
            WorkMinutes = 60,
            WorkHours = 1,
            Status = "Draft"
        });

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-25", "2026-09-25", null, null, CancellationToken.None, compact: true));

        var c = result.GetProperty("timecards").EnumerateArray().Single();
        c.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "WorkDate", "WorkHours", "ProjectCode", "ProjectName", "TaskPath", "TaskWbsCode", "RoleName",
            "RateTypeName", "Status", "Description", "TimecardUid", "Editable");
        c.GetProperty("TaskPath").GetString().Should().Be("Operations > Staff Management");
        c.GetProperty("Editable").GetBoolean().Should().BeTrue();
        result.GetProperty("by_date").GetArrayLength().Should().Be(1, "compact changes only the cards, not the day totals");
    }

    [Fact]
    public async Task GetOverview_TooLongWindow_SaysTheLimitAndHowToSplit()
    {
        var (service, _) = Create();

        var act = () => service.GetResourceOverviewAsync(
            ConnectionId, "10001", "2026-01-01", "2026-12-31", CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("365 days").And.Contain("at most 120 days").And.Contain("shorter window");
    }

    [Fact]
    public void DateWindow_TooLong_SaysTheLimitAndHowToSplit()
    {
        var act = () => Projector.ApiClient.Xml.ProjectorDateHelpers.AssertDateWindow("2025-09-21", "2026-09-30", 366);

        var ex = act.Should().Throw<Projector.Domain.Exceptions.ProjectorApiException>().Which;
        ex.ErrorCode.Should().Be("date_window_exceeded");
        ex.Message.Should().Contain("375 days").And.Contain("at most 366 days").And.Contain("windows of 366 days or less")
            .And.NotContain("8 weeks");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", " ", null)]
    [InlineData("me", null, null)]
    [InlineData(null, " Me ", null)]
    public void GetResource_WithoutAPerson_SaysOwnDataNeedsNoLookup(string? resourceId, string? fullName, string? email)
    {
        var act = () => Projector.Mcp.Server.Tools.ResourceTools.LookupCandidates(resourceId, fullName, email);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("list_timecards").And.Contain("without resource_id").And.Contain("e-mail or full name");
    }

    [Fact]
    public void GetResource_SeveralIdentifiers_AreTriedFastestFirst()
    {
        Projector.Mcp.Server.Tools.ResourceTools.LookupCandidates(null, "Jane Doe", "jane@example.com")
            .Should().Equal("Jane Doe", "jane@example.com");
        Projector.Mcp.Server.Tools.ResourceTools.LookupCandidates(" 10001 ", "Jane Doe", null)
            .Should().Equal("10001", "Jane Doe");
        Projector.Mcp.Server.Tools.ResourceTools.LookupCandidates(null, null, "jane@example.com")
            .Should().Equal("jane@example.com");
    }

    [Fact]
    public async Task ListEngagements_SlowDetails_ReturnsTheListRowsWithANote()
    {
        var (service, soap) = Create();
        service.EngagementDetailBudget = TimeSpan.FromMilliseconds(50);
        soap.Engagements.Add(new Projector.Domain.Engagements.EngagementSummary { EngagementCode = "E005678", EngagementName = "Contoso rollout" });
        soap.EngagementDetailsNeverAnswer = true;

        var result = Json(await service.ListEngagementsAsync(
            ConnectionId, "Contoso", null, null, null, 50, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().Be(1);
        result.GetProperty("engagements")[0].GetProperty("EngagementCode").GetString().Should().Be("E005678");
        result.GetProperty("note").GetString().Should().Contain("list only").And.Contain("get_engagement");
    }

    [Fact]
    public async Task ListEngagements_DetailsInTime_HasNoNote()
    {
        var (service, soap) = Create();
        soap.Engagements.Add(new Projector.Domain.Engagements.EngagementSummary { EngagementCode = "E005678", EngagementName = "Contoso rollout" });

        var result = Json(await service.ListEngagementsAsync(
            ConnectionId, "Contoso", null, null, null, 50, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().Be(1);
        result.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListEngagements_SlowDetails_ManagerSearch_SaysItTimedOut()
    {
        var (service, soap) = Create();
        service.EngagementDetailBudget = TimeSpan.FromMilliseconds(50);
        soap.Engagements.Add(new Projector.Domain.Engagements.EngagementSummary { EngagementCode = "E005678", EngagementName = "Contoso rollout" });
        soap.EngagementDetailsNeverAnswer = true;

        var act = () => service.ListEngagementsAsync(
            ConnectionId, null, "Jane Doe", null, null, 50, CancellationToken.None);

        (await act.Should().ThrowAsync<Projector.Domain.Exceptions.ProjectorApiException>()).Which.ErrorCode
            .Should().Be("projector_timeout", "manager names come only from the details, so the list alone can't answer");
    }

    [Fact]
    public async Task ListEngagements_CallerCancels_IsNotTurnedIntoAPartialResult()
    {
        var (service, soap) = Create();
        soap.Engagements.Add(new Projector.Domain.Engagements.EngagementSummary { EngagementCode = "E005678" });
        soap.EngagementDetailsNeverAnswer = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => service.ListEngagementsAsync(ConnectionId, "Contoso", null, null, null, 50, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListProjectRoles_WithTaskPlan_ReturnsTasksInWbsOrderWithEffortAndPeople()
    {
        var (service, soap) = Create();
        soap.ProjectRoles.AddRange(
        [
            new Projector.Domain.Engagements.ProjectRoleAssignment { ProjectCode = "P005678-001", RoleUid = "r1", RoleName = "Solution Developer", DisplayName = "Jane Doe", ResourceId = "10001" },
            new Projector.Domain.Engagements.ProjectRoleAssignment { ProjectCode = "P005678-001", RoleUid = "r2", RoleName = "Project Coordinator", DisplayName = "Carol Jones", ResourceId = "10002" },
        ]);
        soap.TaskPlan = new Projector.Domain.Engagements.ProjectTaskPlan
        {
            ProjectCode = "P005678-001",
            ProjectName = "Operations",
            PlanStartDate = "2026-10-05",
            PlanEndDate = "2026-10-16",
            MinutesPerDay = 480,
            Tasks =
            [
                PlanTask("t10", "2.10", "Build and Deploy", parent: "t2", duration: 480, predecessors: ["t21"], roles: [("r1", "Solution Developer", 60)]),
                PlanTask("t2", "2", "Ticket 1001 Invoice Automation", parent: null, duration: null),
                PlanTask("t21", "2.2", "Project proposal journal", parent: "t2", duration: 4320, roles: [("r1", "Solution Developer", 120), ("r2", "Project Coordinator", 60)]),
            ]
        };

        var result = Json(await service.ListProjectRolesAsync(
            ConnectionId, ["P005678-001"], CancellationToken.None, includeTaskPlan: true));

        soap.Calls.Should().Equal("ListProjectRolesAsync", "GetProjectTaskPlanAsync(P005678-001)");
        var plan = result.GetProperty("taskPlan");
        plan.GetProperty("taskCount").GetInt32().Should().Be(3);
        plan.GetProperty("totalEffortHours").GetDouble().Should().Be(4);
        var tasks = plan.GetProperty("tasks").EnumerateArray().ToList();
        tasks.Select(t => t.GetProperty("wbsCode").GetString()).Should().Equal("2", "2.2", "2.10");

        tasks[0].GetProperty("summaryTask").GetBoolean().Should().BeTrue();
        tasks[0].GetProperty("effortHours").GetDouble().Should().Be(4, "a summary task shows the total of its sub-tasks");
        tasks[0].GetProperty("durationDays").ValueKind.Should().Be(JsonValueKind.Null);

        tasks[1].GetProperty("taskPath").GetString().Should().Be("Ticket 1001 Invoice Automation > Project proposal journal");
        tasks[1].GetProperty("durationDays").GetDouble().Should().Be(9);
        tasks[1].GetProperty("effortHours").GetDouble().Should().Be(3);
        var roles = tasks[1].GetProperty("roles").EnumerateArray().ToList();
        roles.Select(r => r.GetProperty("displayName").GetString()).Should().Equal("Jane Doe", "Carol Jones");
        roles.Select(r => r.GetProperty("effortHours").GetDouble()).Should().Equal(2, 1);

        tasks[2].GetProperty("predecessors").EnumerateArray().Select(p => p.GetString()).Should().Equal("2.2");
    }

    [Fact]
    public async Task ListProjectRoles_WithoutTaskPlan_MakesNoTaskCall()
    {
        var (service, soap) = Create();

        var result = Json(await service.ListProjectRolesAsync(
            ConnectionId, ["P005678-001", "P005678-002"], CancellationToken.None));

        soap.Calls.Should().Equal("ListProjectRolesAsync");
        result.GetProperty("taskPlan").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListProjectRoles_TaskPlanForSeveralProjects_IsRefusedBeforeAnyCall()
    {
        var (service, soap) = Create();

        var act = () => service.ListProjectRolesAsync(
            ConnectionId, ["P005678-001", "P005678-002"], CancellationToken.None, includeTaskPlan: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("one project at a time");
        soap.Calls.Should().BeEmpty();
    }

    private static Projector.Domain.Engagements.ProjectPlanTask PlanTask(
        string uid, string wbs, string name, string? parent, int? duration,
        string[]? predecessors = null, (string Uid, string Name, int Minutes)[]? roles = null) => new()
    {
        TaskUid = uid,
        ParentTaskUid = parent,
        WbsCode = wbs,
        TaskName = name,
        DurationMinutes = duration,
        PlannedStartDate = "2026-10-05",
        PlannedEndDate = "2026-10-16",
        PredecessorTaskUids = predecessors ?? [],
        Roles = (roles ?? []).Select(r => new Projector.Domain.Engagements.ProjectPlanTaskRole
        {
            RoleUid = r.Uid,
            RoleName = r.Name,
            EffortMinutes = r.Minutes
        }).ToList()
    };

    [Fact]
    public void TimeEntryPrompts_SayAnswersAreNotApproval()
    {
        var rule = Projector.Mcp.Server.Prompts.ProjectorPrompts.SaveConfirmationRule;
        rule.Should().Contain("not approval").And.Contain("Save these N cards?");
        Projector.Mcp.Server.Prompts.ProjectorPrompts.DraftDayTimecards("2026-10-02", rules_file: @"C:\Time\MyTimeRules.md").Text
            .Should().Contain("- Day: 2026-10-02").And.Contain(@"- Rules file: C:\Time\MyTimeRules.md")
            .And.Contain("- Code folders: (not given)").And.NotContain("<!--")
            .And.Contain("are not approval").And.Contain("Save these N cards?")
            .And.Contain("never submit").And.Contain("Read the day back with list_timecards");
        Projector.Mcp.Server.Prompts.ProjectorPrompts.LogTime("2026-09-24", 1, "P005678-001", "Work").Text
            .Should().Contain(rule);
        Projector.Mcp.Server.Prompts.ProjectorPrompts.MyTimecards("2026-09-01", "2026-09-30").Text
            .Should().Contain("without resource_id").And.NotContain("get_resource email");
    }

    [Fact]
    public void ExpenseReportPrompt_DryRunsFirstAndNeverSubmits()
    {
        var text = Projector.Mcp.Server.Prompts.ProjectorPrompts.DraftTripExpenses(
            "Trip to Toronto, Contoso ERP rollout", "Toronto", "2026-07-04", "2026-07-11", @"C:\Receipts\Toronto",
            project_code: " P001234-001 ").Text;
        text.Should().Contain("- Trip name: Trip to Toronto, Contoso ERP rollout").And.Contain("- City: Toronto")
            .And.Contain("- First day: 2026-07-04").And.Contain("- Last day: 2026-07-11")
            .And.Contain("- Project: P001234-001").And.Contain("- Country: (not given)")
            .And.Contain("The report name is the trip name")
            .And.Contain("dry_run = true").And.Contain("not approval").And.Contain("Never submit")
            .And.Contain("receipt_upload").And.Contain("missing_receipt").And.Contain("brief = true");

        var badDates = () => Projector.Mcp.Server.Prompts.ProjectorPrompts.DraftTripExpenses(
            "Trip", "Toronto", "2026-07-11", "2026-07-04", "C:\\Receipts");
        badDates.Should().Throw<ModelContextProtocol.McpException>().WithMessage("*before trip_first_day*");
    }

    [Fact]
    public async Task ListTimecards_Query_FiltersOnWholeWords()
    {
        var (service, soap) = Create();
        soap.Timecards.AddRange(
        [
            Card("1", "S", "Payroll checkpoint with HR"),
            Card("2", "S", "Workplace review"),
            Card("3", "S", "ACE data mapping"),
        ]);

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-25", "2026-09-25", null, null, CancellationToken.None, query: "ace"));

        result.GetProperty("timecards").EnumerateArray().Select(c => c.GetProperty("TimecardUid").GetString())
            .Should().Equal("3");
        result.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CheckAvailability_WithoutPeopleOrHours_ShowsMyCapacity()
    {
        var (service, soap) = Create();

        var result = Json(await service.CheckAvailabilityAsync(
            ConnectionId, [], "2026-09-28", "2026-10-02", null, null, false, CancellationToken.None));

        soap.Calls.Should().ContainSingle().Which.Should().Be("CheckAvailabilityAsync(<none>, 0)", "no people = the signed-in user, no lookup");
        result.GetProperty("people").GetArrayLength().Should().Be(1);
        result.GetProperty("errors").GetArrayLength().Should().Be(0);
        result.GetProperty("required_minutes_per_week").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("note").GetString().Should().Contain("capacity");
    }

    [Fact]
    public async Task CheckAvailability_WithHours_KeepsTheRequirement()
    {
        var (service, soap) = Create();

        var result = Json(await service.CheckAvailabilityAsync(
            ConnectionId, ["me", "10001"], "2026-09-28", "2026-10-02", 20, null, false, CancellationToken.None));

        soap.Calls.Should().Contain("CheckAvailabilityAsync(<none>, 1200)");
        result.GetProperty("required_minutes_per_week").GetDouble().Should().Be(1200);
        result.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListTimecards_AddsHoursPerDayAndStatus()
    {
        var (service, soap) = Create();
        soap.Timecards.AddRange(
        [
            Card("1", "D", minutes: 60, date: "2026-09-24", statusName: "Draft"),
            Card("2", "S", minutes: 90, date: "2026-09-24", statusName: "Submitted"),
            Card("3", "S", minutes: 30, date: "2026-09-23", statusName: "Submitted"),
        ]);

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-23", "2026-09-24", null, null, CancellationToken.None));

        var days = result.GetProperty("by_date").EnumerateArray().ToList();
        days.Select(d => d.GetProperty("date").GetString()).Should().Equal("2026-09-23", "2026-09-24");
        days[1].GetProperty("hours").GetDouble().Should().Be(2.5);
        days[1].GetProperty("card_count").GetInt32().Should().Be(2);
        days[1].GetProperty("hours_by_status").GetProperty("Draft").GetDouble().Should().Be(1);
        days[1].GetProperty("hours_by_status").GetProperty("Submitted").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task ListTimecards_ByDate_ComparesWithTheSchedule_AndListsWorkingDaysWithoutCards()
    {
        var (service, soap) = Create();
        soap.Schedule = TimeEntryTests.Schedule(
            [("2026-09-28", 480, 480), ("2026-09-29", 480, 480), ("2026-09-30", 480, 480), ("2026-10-03", 0, 0)]);
        soap.Timecards.AddRange(
        [
            Card("1", "D", minutes: 360, date: "2026-09-28", statusName: "Draft"),
            Card("2", "D", minutes: 480, date: "2026-09-30", statusName: "Draft"),
        ]);

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-28", "2026-10-03", null, null, CancellationToken.None));

        soap.ScheduleCalls.Should().Equal("<none> 2026-09-28..2026-10-03");
        var days = result.GetProperty("by_date").EnumerateArray().ToList();
        days.Select(d => d.GetProperty("date").GetString()).Should().Equal("2026-09-28", "2026-09-29", "2026-09-30");
        days[0].GetProperty("short_by").GetDouble().Should().Be(2);
        days[1].GetProperty("hours").GetDouble().Should().Be(0);
        days[1].GetProperty("card_count").GetInt32().Should().Be(0);
        days[1].GetProperty("short_by").GetDouble().Should().Be(8, "a working day without cards shows up as missing");
        days[2].GetProperty("expected_hours").GetDouble().Should().Be(8);
        days[2].GetProperty("short_by").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("expected_note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListTimecards_Filtered_LeavesExpectedHoursOut_WithoutReadingTheSchedule()
    {
        var (service, soap) = Create();
        soap.Timecards.Add(Card("1", "D", "ACE mapping", date: "2026-09-28"));

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-28", "2026-10-02", null, null, CancellationToken.None, query: "ace"));

        soap.ScheduleCalls.Should().BeEmpty();
        result.GetProperty("expected_note").GetString().Should().Contain("filter");
        result.GetProperty("by_date")[0].GetProperty("expected_hours").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListTimecards_LongRange_SkipsTheSchedule()
    {
        var (service, soap) = Create();

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-07-01", "2026-09-30", null, null, CancellationToken.None));

        soap.ScheduleCalls.Should().BeEmpty();
        result.GetProperty("expected_note").GetString().Should().Contain("56 days");
    }

    [Fact]
    public async Task ListTimecards_ScheduleFails_KeepsTheCards()
    {
        var (service, soap) = Create();
        soap.ScheduleException = new Projector.Domain.Exceptions.ProjectorApiException("No permission.", "ViewPermissionDenied");
        soap.Timecards.Add(Card("1", "D", date: "2026-09-28", statusName: "Draft"));

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, "10001", "2026-09-28", "2026-10-02", null, null, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().Be(1);
        result.GetProperty("by_date").GetArrayLength().Should().Be(1);
        result.GetProperty("expected_note").GetString().Should().Contain("could not be read");
    }

    [Fact]
    public async Task ListTimecards_GroupByTask_ReturnsOneRowPerTask_MostRecentFirst()
    {
        var (service, soap) = Create();
        static Timecard OnTask(string uid, string task, string path, string? wbs, string description, int minutes, string date) => new()
        {
            TimecardUid = uid, CardStatusCode = "D", Status = "Draft", Description = description, ProjectCode = "P005678-001",
            WorkDate = date, WorkMinutes = minutes, WorkHours = minutes / 60.0, ProjectTaskUid = task, TaskPath = path, TaskWbsCode = wbs
        };
        soap.Timecards.AddRange(
        [
            OnTask("1", "T1", "Build > Analysis", "1.1", "Old style", 60, "2026-09-21"),
            OnTask("2", "T1", "Build > Analysis", "1.1", "Newest style", 30, "2026-09-24"),
            OnTask("3", "T2", "Team Meetings", null, "Meeting", 90, "2026-09-25"),
        ]);

        var result = Json(await service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-21", "2026-09-25", null, null, CancellationToken.None, groupBy: "task"));

        result.TryGetProperty("timecards", out var cards).Should().BeTrue();
        cards.ValueKind.Should().Be(JsonValueKind.Null, "group_by replaces the cards");
        result.GetProperty("count").GetInt32().Should().Be(3);
        result.GetProperty("tasks_count").GetInt32().Should().Be(2);
        var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
        tasks.Select(t => t.GetProperty("task_path").GetString()).Should().Equal("Team Meetings", "Build > Analysis");
        tasks[1].GetProperty("card_count").GetInt32().Should().Be(2);
        tasks[1].GetProperty("hours").GetDouble().Should().Be(1.5);
        tasks[1].GetProperty("first_date").GetString().Should().Be("2026-09-21");
        tasks[1].GetProperty("last_date").GetString().Should().Be("2026-09-24");
        tasks[1].GetProperty("last_description").GetString().Should().Be("Newest style");
        tasks[1].GetProperty("wbs_code").GetString().Should().Be("1.1");
    }

    [Fact]
    public async Task ListTimecards_UnknownGroupBy_IsRefused()
    {
        var (service, _) = Create();

        var act = () => service.ListTimecardsAsync(
            ConnectionId, null, "2026-09-21", "2026-09-25", null, null, CancellationToken.None, groupBy: "week");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("group_by 'week'");
    }

    private static Timecard Card(
        string uid, string status, string description = "Work", int minutes = 60, string date = "2026-09-25", string? statusName = null) => new()
    {
        TimecardUid = uid,
        CardStatusCode = status,
        Description = description,
        ProjectCode = "P005678-001",
        WorkDate = date,
        WorkMinutes = minutes,
        WorkHours = minutes / 60.0,
        Status = statusName
    };

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);

    private static (ProjectorToolService Service, RecordingSoap Soap) Create()
    {
        var store = new InMemoryProjectorConnectionStore();
        store.Save(new ProjectorConnection
        {
            ConnectionId = ConnectionId,
            SessionTicket = "ticket",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            GrantedScope = "allowFullPermissions",
            SoapServiceAuthority = "https://example.invalid",
            RestServiceAuthority = "https://example.invalid"
        });
        var connections = new ProjectorConnectionService(store, new NoTokenClient());
        var soap = RecordingSoap.Create();
        return (new ProjectorToolService(connections, (IProjectorSoapClient)(object)soap), soap);
    }

    /// <summary>Records the calls the tool makes; answers only the few this test needs.</summary>
    public class RecordingSoap : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, ResourceDetail> ResourcesByName { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Timecard> Timecards { get; } = [];

        public List<Projector.Domain.Engagements.EngagementSummary> Engagements { get; } = [];

        public List<Projector.Domain.Engagements.ProjectRoleAssignment> ProjectRoles { get; } = [];

        public Projector.Domain.Engagements.ProjectTaskPlan? TaskPlan { get; set; }

        /// <summary>The schedule list_timecards reads beside the cards (not in <see cref="Calls"/>).</summary>
        public Projector.Domain.Schedule.ResourceSchedule Schedule { get; set; } = new();

        public Exception? ScheduleException { get; set; }

        /// <summary>Schedule reads as "resource start..end".</summary>
        public List<string> ScheduleCalls { get; } = [];

        /// <summary>The detail call hangs until its token is cancelled, like a Projector call that never answers.</summary>
        public bool EngagementDetailsNeverAnswer { get; set; }

        public static RecordingSoap Create() => (RecordingSoap)(object)Create<IProjectorSoapClient, RecordingSoap>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IProjectorSoapClient.ListTimecardsAsync):
                    Calls.Add($"ListTimecardsAsync({(args![1] as string) ?? "<none>"})");
                    return Task.FromResult(new TimecardListResult { Timecards = Timecards.ToList() });
                case nameof(IProjectorSoapClient.GetResourceScheduleAsync):
                    ScheduleCalls.Add($"{(args![1] as string) ?? "<none>"} {args[2]}..{args[3]}");
                    return ScheduleException is null
                        ? Task.FromResult(Schedule)
                        : Task.FromException<Projector.Domain.Schedule.ResourceSchedule>(ScheduleException);
                case nameof(IProjectorSoapClient.CheckAvailabilityAsync):
                    Calls.Add($"CheckAvailabilityAsync({(args![1] as string) ?? "<none>"}, {args[6]})");
                    return Task.FromResult(new Projector.Domain.Availability.AvailabilitySummary { State = "available" });
                case nameof(IProjectorSoapClient.GetResourceAsync):
                    var id = (string)args![1]!;
                    Calls.Add($"GetResourceAsync({id})");
                    return Task.FromResult(ResourcesByName.GetValueOrDefault(id));
                case nameof(IProjectorSoapClient.ListEngagementsAsync):
                    return Task.FromResult(new Projector.Domain.Engagements.EngagementListResult { Engagements = Engagements.ToList() });
                case nameof(IProjectorSoapClient.GetEngagementsByCodeAsync):
                    return EngagementDetailsNeverAnswer
                        ? NeverAnswer((CancellationToken)args![2]!)
                        : Task.FromResult<IReadOnlyList<Projector.Domain.Engagements.EngagementDetail>>([]);
                case nameof(IProjectorSoapClient.ListProjectRolesAsync):
                    Calls.Add("ListProjectRolesAsync");
                    return Task.FromResult(new Projector.Domain.Engagements.ProjectRoleListResult { Roles = ProjectRoles.ToList() });
                case nameof(IProjectorSoapClient.GetProjectTaskPlanAsync):
                    Calls.Add($"GetProjectTaskPlanAsync({args![1]})");
                    return Task.FromResult(TaskPlan);
                case nameof(IProjectorSoapClient.GetProjectsByCodeAsync):
                    return Task.FromResult<IReadOnlyList<Projector.Domain.Engagements.ProjectSummary>>([]);
                default:
                    throw new NotSupportedException($"Unexpected Projector call {targetMethod.Name}");
            }
        }

        private static async Task<IReadOnlyList<Projector.Domain.Engagements.EngagementDetail>> NeverAnswer(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private sealed class NoTokenClient : IProjectorTokenClient
    {
        public Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
            string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectorTokenResponse> RefreshAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
