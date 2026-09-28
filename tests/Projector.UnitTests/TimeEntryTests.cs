using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Projector.ApiClient;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Application.Tools;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Timecards;

namespace Projector.UnitTests;

/// <summary>
/// save_timecard and its lookups: envelopes, parsers (fixtures with the captured shape, invented values),
/// service rules against a fake client, and the no-retry write transport.
/// </summary>
public class TimeEntryTests
{
    private const string ConnectionId = "test";
    private static readonly string FixturesDir = ResolveDir("tests", "Projector.UnitTests", "fixtures");

    private static readonly string[] ForbiddenWriteElements =
    [
        "PwsSubmitTimeCards", "PwsSetTimeCardApprovalWorkflowStatus", "DeleteTimeCards", "DeleteTimeOffCards",
        "SubmitTimeCardIdentity", "SubmitSavedCardsOnlyFlag", "ResourceIdentity", "AdministratorComments"
    ];

    private static XDocument Fixture(string name) => XDocument.Load(Path.Combine(FixturesDir, name));

    private static TimecardSaveRequest NewCard(string? uid = null, string? timestamp = null) => new()
    {
        TimecardUid = uid,
        Timestamp = timestamp,
        WorkDate = "2026-09-24",
        WorkMinutes = 90,
        ProjectCode = "P005678-001",
        TaskUid = "2100000000000000001",
        RoleUid = "2200000000000000001",
        RateTypeUid = "2300000000000000001",
        Description = "Fix build pipeline"
    };

    // ---- Envelopes -------------------------------------------------------------------------

    [Fact]
    public void BuildSaveTimecard_Create_IsDraftAndNeverSubmits()
    {
        var xml = ProjectorEnvelopeBuilders.BuildSaveTimecard("TICKET", NewCard());

        xml.Should().Contain("<tim:CardStatus>D</tim:CardStatus>");
        xml.Should().Contain("<tim:SubmitFlag>false</tim:SubmitFlag>");
        xml.Should().Contain("<tim:SendNotificationEmailFlag>false</tim:SendNotificationEmailFlag>");
        xml.Should().Contain("<com:TimecardType>T</com:TimecardType>");
        xml.Should().Contain("<tim:WorkMinutes>90</tim:WorkMinutes>");
        xml.Should().Contain("<tim:WorkDate>2026-09-24T00:00:00.000Z</tim:WorkDate>");
        xml.Should().NotContain("TimecardUid");
        xml.Should().NotContain("InsertIfNotFoundOnUpdateFlag");
        foreach (var forbidden in ForbiddenWriteElements)
        {
            xml.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public void BuildSaveTimecard_Update_KeepsStatusAndSendsTimestamp()
    {
        var xml = ProjectorEnvelopeBuilders.BuildSaveTimecard("TICKET", NewCard("3000000000000000009", "AAAAADAghmM="));

        xml.Should().Contain("<com:TimecardUid>3000000000000000009</com:TimecardUid>");
        xml.Should().Contain("<tim:Timestamp>AAAAADAghmM=</tim:Timestamp>");
        xml.Should().Contain("<tim:InsertIfNotFoundOnUpdateFlag>false</tim:InsertIfNotFoundOnUpdateFlag>");
        xml.Should().Contain("<tim:WorkDate>", "Projector rejects an update without WorkDate (RequiredFieldMissing)");
        xml.Should().NotContain("CardStatus", "the tool never sets a status on update; Projector decides (Draft)");
        xml.Should().Contain("<tim:SubmitFlag>false</tim:SubmitFlag>");
        foreach (var forbidden in ForbiddenWriteElements)
        {
            xml.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public void BuildSaveTimecard_FollowsDataContractOrder()
    {
        // WCF rejects out-of-order elements ("Element ... is not expected").
        var request = XDocument.Parse(ProjectorEnvelopeBuilders.BuildSaveTimecard("TICKET", NewCard("1", "ts")))
            .Descendants().Single(e => e.Name.LocalName == "serviceRequest");
        request.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "SessionTicket", "EndDate", "InsertIfNotFoundOnUpdateFlag", "SaveTimeCards",
            "SendNotificationEmailFlag", "StartDate", "SubmitFlag");

        var detail = request.Descendants().Single(e => e.Name.LocalName == "PwsTimecardDetail");
        detail.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "ReferenceId", "TimecardType", "TimecardUid", "Description", "WorkDate", "WorkMinutes", "Timestamp",
            "ProjectIdentity", "ProjectRateTypeIdentity", "ProjectTaskIdentity", "RoleIdentity");
    }

    [Fact]
    public void TimeEntryLookups_NeverSendAResource()
    {
        var envelopes = new[]
        {
            ProjectorEnvelopeBuilders.BuildSearchTimeEntryProjects("T", "2026-09-24", "Contoso", "P005678-001"),
            ProjectorEnvelopeBuilders.BuildGetTimeEntryProjectRole("T", "P005678-001", "2026-09-24"),
            ProjectorEnvelopeBuilders.BuildGetTimeEntryParameters("T"),
            ProjectorEnvelopeBuilders.BuildGetOwnTimecard("T", "3000000000000000009", "2026-09-25")
        };
        envelopes.Should().AllSatisfy(xml => xml.Should().NotContain("ResourceIdentity"));
        envelopes[0].Should().Contain("<tim:ListType>T</tim:ListType>");
        envelopes[3].Should().Contain("<com:TimecardUid>3000000000000000009</com:TimecardUid>");
    }

    [Fact]
    public void Source_NeverUsesSubmitApproveOrDelete()
    {
        // The write tool must not be able to submit, approve or delete time, not even by accident later.
        var srcDir = ResolveDir("src");
        var forbidden = new[]
        {
            "\"PwsSubmitTimeCards\"", "\"PwsSetTimeCardApprovalWorkflowStatus\"", "\"DeleteTimeCards\"",
            "\"SubmitTimeCardIdentity\"", "\"SubmitSavedCardsOnlyFlag\"", "\"PreserveTimeCardStatusFlag\""
        };
        var offenders = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token)).Select(t => $"{f}: {t}"))
            .ToList();
        offenders.Should().BeEmpty();
    }

    // ---- Parsers ---------------------------------------------------------------------------

    [Fact]
    public void ParseSearchProjects_ReadsProjectsAndRoles()
    {
        var projects = ProjectorTimeEntryParsers.ParseSearchProjects(Fixture("time_entry_search_projects.xml"));
        projects.Should().HaveCount(2);
        var contoso = projects[0];
        contoso.ProjectCode.Should().Be("P005678-001");
        contoso.ProjectName.Should().Be("Contoso - Development Services");
        contoso.ClientName.Should().Be("Contoso");
        contoso.Billable.Should().BeTrue();
        contoso.UnavailableReasonCode.Should().BeNull();
        contoso.Roles.Should().ContainSingle().Which.Should().Be(
            new TimeEntryRole("2200000000000000001", "System Engineer", "2026-09-09", "2026-12-31"));
        projects[1].Roles.Should().BeEmpty();
        projects[1].Billable.Should().BeFalse();
    }

    [Fact]
    public void ParseTimeEntryProject_ReadsTasksAndRateTypesPerTaskType()
    {
        var setup = ProjectorTimeEntryParsers.ParseTimeEntryProject(Fixture("time_entry_project_role.xml"))!;
        setup.ProjectCode.Should().Be("P005678-001");
        setup.OpenForTime.Should().BeTrue();
        setup.DescriptionRequired.Should().BeTrue();
        setup.Udf1Treatment.Should().Be("A");
        setup.RateTypes.Select(r => r.Name).Should().Equal("Billable", "Non-Chargeable");

        setup.Tasks.Should().HaveCount(3);
        var dev = setup.Tasks[0];
        dev.Name.Should().Be("Development");
        dev.OpenForTime.Should().BeTrue();
        dev.TaskTypeName.Should().Be("Development Services");
        dev.AllowedRateTypes.Select(r => r.Name).Should().Equal("Billable");
        dev.DefaultRateTypeUid.Should().Be("2300000000000000001");
        setup.Tasks[1].AllowedRateTypes.Should().BeEmpty("the task has no task type");
        setup.Tasks[2].OpenForTime.Should().BeFalse();
    }

    [Fact]
    public void ParseTimeEntryParameters_ReadsIncrementAndUdfs()
    {
        var rules = ProjectorTimeEntryParsers.ParseTimeEntryParameters(Fixture("time_entry_parameters.xml"));
        rules.ReportingTimeIncrementMinutes.Should().Be(15);
        rules.RequireLocation.Should().BeFalse();
        rules.Enforce24HourDailyLimit.Should().BeTrue();
        rules.Udf1.Should().NotBeNull();
        rules.Udf1!.Name.Should().Be("Reason if Non Billable");
        rules.Udf1.Uid.Should().Be("2500000000000000001");
        rules.Udf1.DataType.Should().Be("T");
        rules.Udf1.Required.Should().BeFalse();
        rules.Udf2.Should().BeNull();
    }

    [Fact]
    public void ParseOwnTimecard_ReadsIdsStatusAndTimestamp()
    {
        var card = ProjectorTimeEntryParsers.ParseOwnTimecard(Fixture("own_timecard_rejected.xml"), "3000000000000000009")!;
        card.CardStatusCode.Should().Be("R");
        card.WorkDate.Should().Be("2026-09-25");
        card.WorkMinutes.Should().Be(75);
        card.ProjectCode.Should().Be("P005678-001");
        card.TaskUid.Should().Be("2100000000000000001");
        card.RoleUid.Should().Be("2200000000000000001");
        card.RateTypeUid.Should().Be("2300000000000000001");
        card.Timestamp.Should().Be("AAAAADAghmM=");

        ProjectorTimeEntryParsers.ParseOwnTimecard(Fixture("own_timecard_rejected.xml"), "999").Should().BeNull();
    }

    [Fact]
    public void ParseSaveResult_ReadsNewDraftCard()
    {
        var result = ProjectorTimeEntryParsers.ParseSaveResult(Fixture("save_timecards_ok.xml"));
        result.TimecardUid.Should().Be("3000000000000000042");
        result.CardStatusCode.Should().Be("D");
        result.WorkMinutes.Should().Be(90);
        result.SubmittedFlag.Should().BeFalse();
    }

    [Fact]
    public void ParseSaveResult_ThrowsPerCardErrorBeforeSummary()
    {
        var act = () => ProjectorTimeEntryParsers.ParseSaveResult(Fixture("save_timecards_error.xml"));
        act.Should().Throw<ProjectorApiException>().Which.ErrorCode.Should().Be("TimecardHasBeenChanged");
    }

    [Fact]
    public void ParseTimeCards_ReturnsIdsForUpdates()
    {
        var cards = ProjectorResponseParsers.ParseTimeCards(Fixture("timecards.xml"));
        var card = cards.First(c => c.TimecardUid == "3000000000000000001");
        card.ProjectTaskUid.Should().Be("2100000000000000001");
        card.ProjectRoleUid.Should().Be("2200000000000000001");
        card.ProjectRateTypeUid.Should().Be("2300000000000000001");
    }

    // ---- Service rules ---------------------------------------------------------------------

    [Theory]
    [InlineData(1.5, 15, 90)]
    [InlineData(0.25, 15, 15)]
    [InlineData(8, 15, 480)]
    [InlineData(0.1, 6, 6)]
    public void ToMinutes_AcceptsWholeIncrements(double hours, int increment, int expected) =>
        TimeEntryToolService.ToMinutes(hours, increment).Should().Be(expected);

    [Theory]
    [InlineData(1.3, 15)]
    [InlineData(0.01, 1)]
    public void ToMinutes_RejectsOffIncrement(double hours, int increment)
    {
        var act = () => TimeEntryToolService.ToMinutes(hours, increment);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Save_Create_ResolvesNamesAndSavesDraftOnce()
    {
        var (service, fake) = CreateService();
        dynamic result = await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);

        fake.Saves.Should().ContainSingle();
        var saved = fake.Saves[0];
        saved.TimecardUid.Should().BeNull();
        saved.WorkMinutes.Should().Be(90);
        saved.TaskUid.Should().Be("2100000000000000001");
        saved.RoleUid.Should().Be("2200000000000000001");
        saved.RateTypeUid.Should().Be("2300000000000000001");
        ((string)result.action).Should().Be("created");
        ((bool)result.submitted).Should().BeFalse();
    }

    [Fact]
    public async Task Save_Update_OfRejectedCard_SendsTimestamp()
    {
        var (service, fake) = CreateService();
        fake.Card = ProjectorTimeEntryParsers.ParseOwnTimecard(Fixture("own_timecard_rejected.xml"), "3000000000000000009");
        fake.SaveResult = new TimecardSaveResult { TimecardUid = "3000000000000000009", CardStatusCode = "D" };

        dynamic result = await service.SaveTimecardAsync(
            ConnectionId, Input(uid: "3000000000000000009", date: "2026-09-25"), CancellationToken.None);

        fake.Saves.Should().ContainSingle().Which.Timestamp.Should().Be("AAAAADAghmM=");
        ((string)result.action).Should().Be("updated");
        ((string)result.timecard.status).Should().Be("Draft", "Projector moves a saved Rejected card back to Draft");
        ((string)result.note).Should().Contain("was Rejected and is now a Draft");
        ((object?)result.warnings).Should().BeNull();
    }

    [Theory]
    [InlineData("S")]
    [InlineData("A")]
    [InlineData("B")]
    public async Task Save_Update_RefusesCardsThatAreNotDraftOrRejected(string status)
    {
        var (service, fake) = CreateService();
        fake.Card = new OwnTimecard { TimecardUid = "9", CardStatusCode = status, ProjectCode = "P005678-001" };

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(uid: "9"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("timecard_not_editable");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_Update_RefusesCardNotOnOwnTimeSheet()
    {
        var (service, fake) = CreateService();
        fake.Card = null; // not returned for the caller = someone else's card, or a wrong date

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(uid: "9"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("timecard_not_found");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_Update_RefusesMovingToAnotherProject()
    {
        var (service, fake) = CreateService();
        fake.Card = new OwnTimecard { TimecardUid = "9", CardStatusCode = "D", ProjectCode = "C000900-002" };

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(uid: "9"), CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("timecard_move_not_supported");
    }

    [Theory]
    [InlineData("Non-Chargeable", "invalid_rate_type")] // project rate type the task's type does not allow
    [InlineData("Nope", "invalid_rate_type")]
    public async Task Save_RejectsRateTypeTheTaskDoesNotAllow(string rateType, string code)
    {
        var (service, fake) = CreateService();
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(rateType: rateType), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be(code);
        error.Message.Should().Contain("Billable");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_TaskWithoutTypeUsesProjectRateTypes()
    {
        var (service, fake) = CreateService();
        await service.SaveTimecardAsync(
            ConnectionId, Input(task: "Project Management", rateType: "Non-Chargeable"), CancellationToken.None);
        fake.Saves.Should().ContainSingle().Which.RateTypeUid.Should().Be("2300000000000000002");
    }

    [Fact]
    public async Task Save_RefusesClosedTask()
    {
        var (service, _) = CreateService();
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(task: "Discovery"), CancellationToken.None);
        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("task_closed");
    }

    [Fact]
    public async Task Save_RefusesProjectWithoutRoleForUser()
    {
        var (service, fake) = CreateService();
        fake.Projects = [];
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);
        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("no_role_on_project");
    }

    [Fact]
    public void Resolve_ReportsAmbiguousNames()
    {
        var items = new[] { new TimeEntryRateType("1", "Billable"), new TimeEntryRateType("2", "billable") };
        var act = () => TimeEntryToolService.Resolve(items, "Billable", r => r.Uid, r => r.Name, "rate_type", "P1");
        act.Should().Throw<ProjectorApiException>().Which.ErrorCode.Should().Be("ambiguous_rate_type");
        TimeEntryToolService.Resolve(items, "2", r => r.Uid, r => r.Name, "rate_type", "P1").Uid.Should().Be("2");
    }

    [Theory]
    [InlineData(1.5, "", "narrative")]
    [InlineData(0, "Work", "hours")]
    [InlineData(25, "Work", "hours")]
    [InlineData(1.3, "Work", "15-minute")]
    public async Task Save_ValidatesInputBeforeWriting(double hours, string narrative, string mentions)
    {
        var (service, fake) = CreateService();
        var act = () => service.SaveTimecardAsync(
            ConnectionId, Input(hours: hours, narrative: narrative), CancellationToken.None);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(mentions);
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_RejectsTooLongNarrative()
    {
        var (service, _) = CreateService();
        var act = () => service.SaveTimecardAsync(
            ConnectionId, Input(narrative: new string('x', 1001)), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_MapsWebServicesPermissionError()
    {
        var (service, fake) = CreateService();
        fake.SaveException = new ProjectorApiException("You do not have permission to update this item.", "UpdatePermissionDenied");

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("web_services_access_view_only");
        error.Message.Should().StartWith("Nothing was saved.");
        error.Message.Should().Contain("Web Services Access V (View), not U (Update)");
        error.Message.Should().Contain("ask your Projector PSA administrator");

        // This is what the agent (Copilot or another MCP client) receives.
        var result = Projector.Mcp.Server.Tools.AgentTools.ToError(error);
        result.IsError.Should().BeTrue();
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        text.Should().Contain("\"error\":\"web_services_access_view_only\"");
        text.Should().Contain("Web Services Access V (View), not U (Update)");
    }

    [Fact]
    public async Task Save_OutcomeUnknown_IsNotRetried()
    {
        var (service, fake) = CreateService();
        fake.SaveException = new ProjectorApiException("timeout", ProjectorTimeEntryClient.WriteOutcomeUnknown);

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("write_outcome_unknown");
        fake.SaveCalls.Should().Be(1);
    }

    [Fact]
    public async Task WriteTransport_SendsSaveExactlyOnce()
    {
        // Registered the same way as production; only the primary handler is swapped for a failing counter.
        var handler = new CountingHandler(new HttpRequestException("connection reset"));
        var services = new ServiceCollection().AddLogging();
        services.AddProjectorApiClient();
        services.AddHttpClient<ProjectorSoapWriteHttp>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IProjectorTimeEntryClient>();

        var act = () => client.SaveTimecardAsync(Connection(), NewCard());

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("write_outcome_unknown");
        handler.Calls.Should().Be(1, "a save that may have reached Projector must never be resent automatically");
    }

    // ---- Task paths, paging, cache, day total, duplicates (v0.6.0) --------------------------

    [Fact]
    public void TaskPaths_JoinParentsAndSurviveBadChains()
    {
        var paths = TaskPaths.Build(
        [
            ("1", "Story", null),
            ("2", "Analysis", "1"),
            ("3", "Sub", "2"),
            ("4", "Orphan", "999"),
            ("5", "Loop A", "6"),
            ("6", "Loop B", "5")
        ]);

        paths["3"].Should().Be("Story > Analysis > Sub");
        paths["4"].Should().Be("Orphan", "a parent outside the list is skipped");
        paths["5"].Should().Be("Loop B > Loop A", "a cycle stops instead of looping forever");
    }

    [Fact]
    public void ParseTimeEntryProject_BuildsTaskPathsFromParentUids()
    {
        var setup = ProjectorTimeEntryParsers.ParseTimeEntryProject(Fixture("time_entry_project_tree.xml"))!;
        var analysis = setup.Tasks.Where(t => t.Name == "Analysis & Design").ToList();

        analysis.Should().HaveCount(2);
        analysis[0].Path.Should().Be("User Story 101: Export totals do not match > Analysis & Design");
        analysis[0].WbsCode.Should().Be("1.1");
        analysis[0].ParentTaskUid.Should().Be("2100000000000000011");
        analysis[1].Path.Should().StartWith("User Story 202");
        setup.Tasks[0].Path.Should().Be(setup.Tasks[0].Name, "a top-level task's path is its name");
    }

    [Fact]
    public void ParseTimeCards_AddsTaskPathAndWbsFromTheProjectTree()
    {
        var doc = XDocument.Parse("""
            <Envelope><Body><PwsGetTimeCardsResult><TimeEntryProjects><PwsTimeEntryProject>
              <ProjectDescriptor><ProjectCode>P005678-001</ProjectCode><ProjectName>Contoso</ProjectName></ProjectDescriptor>
              <ProjectTasks>
                <PwsProjectTask><ProjectTaskUid>11</ProjectTaskUid><Name>User Story 101</Name><WbsCode>1</WbsCode></PwsProjectTask>
                <PwsProjectTask><ProjectTaskUid>12</ProjectTaskUid><Name>Analysis &amp; Design</Name>
                  <ParentProjectTaskIdentity><ProjectTaskUid>11</ProjectTaskUid></ParentProjectTaskIdentity><WbsCode>1.1</WbsCode></PwsProjectTask>
              </ProjectTasks>
              <TimeCards><PwsTimecardDetail>
                <TimecardUid>900</TimecardUid><WorkDate>2026-09-25T00:00:00</WorkDate><WorkMinutes>30</WorkMinutes><CardStatus>D</CardStatus>
                <ProjectTaskIdentity><ProjectTaskUid>12</ProjectTaskUid></ProjectTaskIdentity>
              </PwsTimecardDetail></TimeCards>
            </PwsTimeEntryProject></TimeEntryProjects></PwsGetTimeCardsResult></Body></Envelope>
            """);

        var card = ProjectorResponseParsers.ParseTimeCards(doc).Single();
        card.TaskName.Should().Be("Analysis & Design");
        card.TaskPath.Should().Be("User Story 101 > Analysis & Design");
        card.TaskWbsCode.Should().Be("1.1");
    }

    [Fact]
    public void CardAndScheduleReads_WithoutResource_SendNoResourceIdentity()
    {
        // Projector applies these to the caller when no resource is sent (checked live 2026-09-26).
        ProjectorEnvelopeBuilders.BuildGetTimeCards("T", null, "2026-09-25", "2026-09-25")
            .Should().NotContain("ResourceIdentity");
        ProjectorEnvelopeBuilders.BuildGetResourceSchedule("T", null, "2026-09-25", "2026-09-25")
            .Should().NotContain("ResourceIdentity");
        ProjectorEnvelopeBuilders.BuildGetTimeCards("T", "10001", "2026-09-25", "2026-09-25")
            .Should().Contain("<com:ResourceReferenceSystemId>10001</com:ResourceReferenceSystemId>");
    }

    [Fact]
    public async Task Options_QueryMatchesPathOrWbs_AndPages()
    {
        var (service, fake) = CreateService(tree: true);

        var byStory = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None, query: "101"));
        byStory.GetProperty("tasks_total").GetInt32().Should().Be(2);
        byStory.GetProperty("tasks")[0].GetProperty("task_path").GetString()
            .Should().Be("User Story 101: Export totals do not match > Analysis & Design");

        var byWbs = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None, query: "2.2"));
        byWbs.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("task_uid").GetString())
            .Should().Equal("2100000000000000023");

        var page = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None, maxTasks: 3));
        page.GetProperty("tasks_count").GetInt32().Should().Be(3);
        page.GetProperty("tasks_total").GetInt32().Should().Be(4, "only tasks open for time are listed");
        page.GetProperty("tasks_has_more").GetBoolean().Should().BeTrue();
        page.GetProperty("tasks_next_offset").GetInt32().Should().Be(3);

        fake.Calls["setup"].Should().Be(1, "filtering and paging reuse the cached project setup");
        fake.Calls["projects"].Should().Be(1);
        fake.Calls["rules"].Should().Be(1);
    }

    [Fact]
    public async Task Options_ListsSharedRateTypesOnce()
    {
        var (service, _) = CreateService(tree: true);
        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));

        options.GetProperty("rate_types").EnumerateArray().Select(r => r.GetProperty("rate_type_name").GetString())
            .Should().Equal("Billable");
        options.GetProperty("tasks").EnumerateArray()
            .Should().AllSatisfy(t => t.TryGetProperty("rate_types", out _).Should().BeFalse());
    }

    [Fact]
    public async Task Options_ListsRateTypesOnATaskOnlyWhenTheyDifferFromTheCommonSet()
    {
        var (service, _) = CreateService();
        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));

        // Open tasks: Development (Billable) and Project Management (no task type: Billable, Non-Chargeable).
        var common = options.GetProperty("rate_types").EnumerateArray().Select(r => r.GetProperty("rate_type_name").GetString()).ToList();
        var tasks = options.GetProperty("tasks").EnumerateArray().ToList();
        tasks.Count(t => t.TryGetProperty("rate_types", out _)).Should().Be(1, "only the task that differs lists its own");
        var own = tasks.Single(t => t.TryGetProperty("rate_types", out _)).GetProperty("rate_types").EnumerateArray()
            .Select(r => r.GetProperty("rate_type_name").GetString()).ToList();
        own.Should().NotEqual(common);
    }

    [Theory]
    [InlineData("User Story 202: Invoice layout > Analysis & Design", "2100000000000000022")]
    [InlineData("User Story 202: Invoice layout>Analysis & Design", "2100000000000000022")]
    [InlineData("1.2", "2100000000000000013")]
    [InlineData("2100000000000000023", "2100000000000000023")]
    public async Task Save_ResolvesTaskByPathWbsOrUid(string task, string expectedUid)
    {
        var (service, fake) = CreateService(tree: true);
        await service.SaveTimecardAsync(ConnectionId, Input(task: task), CancellationToken.None);
        fake.Saves.Should().ContainSingle().Which.TaskUid.Should().Be(expectedUid);
    }

    [Fact]
    public async Task Save_AmbiguousTaskName_ListsPaths()
    {
        var (service, fake) = CreateService(tree: true);
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(task: "Development"), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("ambiguous_task");
        error.Message.Should().Contain("User Story 101: Export totals do not match > Development (WBS 1.2");
        error.Message.Should().Contain("User Story 202: Invoice layout > Development (WBS 2.2");
        fake.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_UnknownTask_PointsToOptionsQuery()
    {
        var (service, _) = CreateService(tree: true);
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(task: "Testing"), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("invalid_task");
        error.Message.Should().Contain("get_timecard_options").And.Contain("query");
    }

    [Fact]
    public async Task Save_ReportsDayTotal_AndKeepsItCurrentAcrossSaves()
    {
        var (service, fake) = CreateService();
        fake.DayCards = [DayCard("800", minutes: 60, task: "2100000000000000002", narrative: "Weekly status call")];

        var first = Json(await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None));
        first.GetProperty("day").GetProperty("total_hours").GetDouble().Should().Be(2.5);
        first.GetProperty("day").GetProperty("card_count").GetInt32().Should().Be(2);
        first.TryGetProperty("warnings", out _).Should().BeFalse();

        fake.SaveResult = new TimecardSaveResult { TimecardUid = "3000000000000000043", CardStatusCode = "D" };
        var second = Json(await service.SaveTimecardAsync(
            ConnectionId, Input(hours: 0.5, narrative: "Code review of the release branch"), CancellationToken.None));
        second.GetProperty("day").GetProperty("total_hours").GetDouble().Should().Be(3);
        second.GetProperty("day").GetProperty("card_count").GetInt32().Should().Be(3);

        fake.Calls["day_cards"].Should().Be(1, "the day's cards are read once and updated after each save");
        fake.Calls["setup"].Should().Be(1);
        fake.Calls["projects"].Should().Be(1);
        fake.Calls["rules"].Should().Be(1);
    }

    [Theory]
    [InlineData(90, "Fix build pipeline")] // same card again
    [InlineData(30, "Fix the build pipeline")] // similar narrative, other length
    public async Task Save_WarnsAboutLikelyDuplicate_ButStillSaves(int existingMinutes, string existingNarrative)
    {
        var (service, fake) = CreateService();
        fake.DayCards = [DayCard("801", existingMinutes, "2100000000000000001", existingNarrative)];

        var result = Json(await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None));

        fake.Saves.Should().ContainSingle();
        result.GetProperty("warnings")[0].GetString().Should().Contain("Possible duplicate: card 801");
    }

    [Theory]
    [InlineData("Something else entirely")]
    [InlineData("Personal meeting with John Smith")] // round 2: two 1:1s on the same task, same hours
    public async Task Save_NoDuplicateWarning_ForSameTaskAndHoursButOtherWork(string existingNarrative)
    {
        var (service, fake) = CreateService();
        fake.DayCards = [DayCard("804", 90, "2100000000000000001", existingNarrative)];

        var result = Json(await service.SaveTimecardAsync(
            ConnectionId, Input(narrative: "Personal meeting with Maria Garcia"), CancellationToken.None));

        result.TryGetProperty("warnings", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Save_NoDuplicateWarning_ForOtherTaskOrUpdate()
    {
        var (service, fake) = CreateService();
        fake.DayCards = [DayCard("802", 90, "2100000000000000002", "Fix build pipeline")];
        var other = Json(await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None));
        other.TryGetProperty("warnings", out _).Should().BeFalse("the existing card is on another task");

        var (updating, fake2) = CreateService();
        fake2.Card = new OwnTimecard { TimecardUid = "803", CardStatusCode = "D", ProjectCode = "P005678-001" };
        fake2.DayCards = [DayCard("803", 90, "2100000000000000001", "Fix build pipeline")];
        fake2.SaveResult = new TimecardSaveResult { TimecardUid = "803", CardStatusCode = "D" };
        var updated = Json(await updating.SaveTimecardAsync(ConnectionId, Input(uid: "803"), CancellationToken.None));
        updated.TryGetProperty("warnings", out _).Should().BeFalse("an update is not a duplicate of itself");
        updated.GetProperty("day").GetProperty("card_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Save_DayReadFails_SavesAndWarns()
    {
        var (service, fake) = CreateService();
        fake.DayCardsException = new ProjectorApiException("boom", "SomeError");

        var result = Json(await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None));

        fake.Saves.Should().ContainSingle();
        result.TryGetProperty("day", out _).Should().BeFalse();
        result.GetProperty("warnings")[0].GetString().Should().Contain("no duplicate check or day total");
    }

    [Fact]
    public async Task Cache_IsPerUser()
    {
        var cache = new TimeEntryCache();
        var loads = 0;
        Task<string> Load() => Task.FromResult($"value {++loads}");
        var alice = Connection("a", tenant: "t1", oid: "alice");
        var aliceOtherSession = Connection("a2", tenant: "t1", oid: "alice");
        var bob = Connection("b", tenant: "t1", oid: "bob");

        (await cache.GetOrLoadAsync(alice, "k", "x", TimeSpan.FromMinutes(1), Load)).Should().Be("value 1");
        (await cache.GetOrLoadAsync(aliceOtherSession, "k", "x", TimeSpan.FromMinutes(1), Load)).Should().Be("value 1");
        (await cache.GetOrLoadAsync(bob, "k", "x", TimeSpan.FromMinutes(1), Load)).Should().Be("value 2");

        cache.ClearUser(alice);
        (await cache.GetOrLoadAsync(alice, "k", "x", TimeSpan.FromMinutes(1), Load)).Should().Be("value 3");
        (await cache.GetOrLoadAsync(bob, "k", "x", TimeSpan.FromMinutes(1), Load)).Should().Be("value 2");
    }

    [Fact]
    public async Task ListTimeProjects_FiltersCachedListAndMarksChargeable()
    {
        var (service, fake) = CreateService();

        var all = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None));
        var chargeable = all.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("chargeable").GetBoolean()).ToList();
        chargeable.Should().Equal(true, false);

        var page = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 1, CancellationToken.None, offset: 1));
        page.GetProperty("projects")[0].GetProperty("chargeable").GetBoolean().Should().BeFalse();
        page.GetProperty("has_more").GetBoolean().Should().BeFalse();

        var filtered = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "contoso", 50, CancellationToken.None));
        filtered.GetProperty("total").GetInt32().Should().Be(1);

        fake.Calls["projects"].Should().Be(1, "query and paging filter the cached list");
    }

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result,
        new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private static Timecard DayCard(string uid, int minutes, string task, string narrative) => new()
    {
        TimecardUid = uid,
        ProjectCode = "P005678-001",
        WorkDate = "2026-09-24",
        WorkMinutes = minutes,
        WorkHours = minutes / 60.0,
        Status = "Draft",
        CardStatusCode = "D",
        ProjectTaskUid = task,
        Description = narrative
    };

    // ---- Helpers ---------------------------------------------------------------------------

    private static SaveTimecardInput Input(
        string? uid = null,
        string date = "2026-09-24",
        double hours = 1.5,
        string task = "development",
        string role = "System Engineer",
        string rateType = "Billable",
        string narrative = "Fix build pipeline") =>
        new(date, hours, "P005678-001", task, role, rateType, narrative, uid);

    private static ProjectorConnection Connection(string id = ConnectionId, string? tenant = null, string? oid = null) => new()
    {
        ConnectionId = id,
        TenantId = tenant,
        EntraObjectId = oid,
        SessionTicket = "ticket",
        RefreshToken = "refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        GrantedScope = "allowFullPermissions",
        SoapServiceAuthority = "https://example.invalid",
        RestServiceAuthority = "https://example.invalid"
    };

    private static (TimeEntryToolService Service, FakeTimeEntryClient Fake) CreateService(bool tree = false)
    {
        var store = new InMemoryProjectorConnectionStore();
        store.Save(Connection());
        var connections = new ProjectorConnectionService(store, new NoRefreshTokenClient());
        var fake = new FakeTimeEntryClient
        {
            Projects = ProjectorTimeEntryParsers.ParseSearchProjects(Fixture("time_entry_search_projects.xml")).ToList(),
            Setup = ProjectorTimeEntryParsers.ParseTimeEntryProject(
                Fixture(tree ? "time_entry_project_tree.xml" : "time_entry_project_role.xml")),
            Parameters = ProjectorTimeEntryParsers.ParseTimeEntryParameters(Fixture("time_entry_parameters.xml"))
        };
        return (new TimeEntryToolService(connections, fake, new TimeEntryCache(), NullLogger<TimeEntryToolService>.Instance), fake);
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

    private sealed class FakeTimeEntryClient : IProjectorTimeEntryClient
    {
        public List<TimeEntryProjectSummary> Projects { get; set; } = [];
        public TimeEntryProjectSetup? Setup { get; set; }
        public TimeEntryParameters Parameters { get; set; } = new();
        public OwnTimecard? Card { get; set; }
        public TimecardSaveResult SaveResult { get; set; } = new() { TimecardUid = "3000000000000000042", CardStatusCode = "D" };
        public Exception? SaveException { get; set; }
        public List<Timecard> DayCards { get; set; } = [];
        public Exception? DayCardsException { get; set; }
        public List<TimecardSaveRequest> Saves { get; } = [];
        public int SaveCalls { get; private set; }

        /// <summary>Projector read calls per kind, to check what the cache saves.</summary>
        public Dictionary<string, int> Calls { get; } = new() { ["projects"] = 0, ["setup"] = 0, ["rules"] = 0, ["day_cards"] = 0 };

        public Task<IReadOnlyList<TimeEntryProjectSummary>> SearchTimeEntryProjectsAsync(
            ProjectorConnection connection, string workDate, string? query = null, string? projectCode = null,
            CancellationToken cancellationToken = default)
        {
            Calls["projects"]++;
            return Task.FromResult<IReadOnlyList<TimeEntryProjectSummary>>(Projects
                .Where(p => projectCode is null || string.Equals(p.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        public Task<TimeEntryProjectSetup?> GetTimeEntryProjectAsync(
            ProjectorConnection connection, string projectCode, string workDate, CancellationToken cancellationToken = default)
        {
            Calls["setup"]++;
            return Task.FromResult(Setup);
        }

        public Task<TimeEntryParameters> GetTimeEntryParametersAsync(
            ProjectorConnection connection, CancellationToken cancellationToken = default)
        {
            Calls["rules"]++;
            return Task.FromResult(Parameters);
        }

        public Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
            ProjectorConnection connection, string workDate, CancellationToken cancellationToken = default)
        {
            Calls["day_cards"]++;
            return DayCardsException is not null
                ? Task.FromException<IReadOnlyList<Timecard>>(DayCardsException)
                : Task.FromResult<IReadOnlyList<Timecard>>(DayCards.ToList());
        }

        public Task<OwnTimecard?> GetOwnTimecardAsync(
            ProjectorConnection connection, string timecardUid, string workDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Card?.TimecardUid == timecardUid ? Card : null);

        public Task<TimecardSaveResult> SaveTimecardAsync(
            ProjectorConnection connection, TimecardSaveRequest request, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (SaveException is not null)
            {
                throw SaveException;
            }

            Saves.Add(request);
            return Task.FromResult(SaveResult);
        }
    }

    private sealed class NoRefreshTokenClient : IProjectorTokenClient
    {
        public Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
            string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectorTokenResponse> RefreshAsync(
            ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingHandler(Exception failure) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }
}
