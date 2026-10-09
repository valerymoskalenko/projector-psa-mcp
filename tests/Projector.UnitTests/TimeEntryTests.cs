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
using Projector.Domain.Schedule;
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
        setup.TaskTypeDefaultRateTypeUids.Should().Equal("2300000000000000001");
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

    [Fact]
    public async Task Save_AlwaysUsesTheTaskDefaultRateType()
    {
        var (service, fake) = CreateService();
        dynamic result = await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);

        fake.Saves.Should().ContainSingle().Which.RateTypeUid.Should().Be("2300000000000000001");
        ((string)result.timecard.rate_type).Should().Be("Billable");
    }

    [Fact]
    public async Task Save_Update_ResetsRateTypeToTheTaskDefault()
    {
        // The card was saved with another rate type (e.g. by hand in Projector); an update from the tool uses the
        // task's default, also when the card moves to another task.
        var (service, fake) = CreateService();
        fake.Card = new OwnTimecard
        {
            TimecardUid = "9", CardStatusCode = "D", ProjectCode = "P005678-001",
            TaskUid = "2100000000000000002", RateTypeUid = "2300000000000000002"
        };
        fake.SaveResult = new TimecardSaveResult { TimecardUid = "9", CardStatusCode = "D" };

        await service.SaveTimecardAsync(ConnectionId, Input(uid: "9", task: "Development"), CancellationToken.None);

        fake.Saves.Should().ContainSingle().Which.RateTypeUid.Should().Be("2300000000000000001");
    }

    [Fact]
    public async Task Save_TaskWithoutType_UsesTheProjectsCommonDefault()
    {
        // "Project Management" has no task type (no default) and two allowed rate types; every task type on the
        // project defaults to Billable, so that is used (seen live on a project where 30 of 39 tasks have no type).
        var (service, fake) = CreateService();
        await service.SaveTimecardAsync(ConnectionId, Input(task: "Project Management"), CancellationToken.None);
        fake.Saves.Should().ContainSingle().Which.RateTypeUid.Should().Be("2300000000000000001");
    }

    [Fact]
    public void DefaultRateType_RefusesWhenTaskTypesDisagree()
    {
        var billable = new TimeEntryRateType("1", "Billable");
        var internalRate = new TimeEntryRateType("2", "Internal");
        var setup = new TimeEntryProjectSetup
        {
            ProjectCode = "P1",
            RateTypes = [billable, internalRate],
            TaskTypeDefaultRateTypeUids = ["1", "2"]
        };
        var untyped = new TimeEntryTask { Uid = "10", Name = "Support" };

        var act = () => TimeEntryToolService.DefaultRateType(untyped, setup, "P1");

        var error = act.Should().Throw<ProjectorApiException>().Which;
        error.ErrorCode.Should().Be("no_default_rate_type");
        error.Message.Should().Contain("enter this card in Projector");
    }

    [Fact]
    public void DefaultRateType_WithoutDefault_UsesTheOnlyAllowedOne()
    {
        var only = new TimeEntryRateType("2300000000000000009", "Internal");
        var setup = new TimeEntryProjectSetup { ProjectCode = "P1", RateTypes = [only] };
        var untyped = new TimeEntryTask { Uid = "1", Name = "Support" };

        TimeEntryToolService.DefaultRateType(untyped, setup, "P1").Should().Be(only);
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
        fake.Assignments = new TaskAssignments(
            false, new Dictionary<string, IReadOnlySet<string>>(), "Dana Whitfield", "dana.whitfield@example.com");
        var act = () => service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("no_role_on_project");
        error.Message.Should().Contain("Dana Whitfield (dana.whitfield@example.com)");
    }

    [Fact]
    public async Task Options_WithoutRole_SayNoRoleAndNameTheProjectManager()
    {
        var (service, fake) = CreateService();
        var withRole = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));
        withRole.TryGetProperty("no_role", out _).Should().BeFalse();
        fake.Calls["assignments"].Should().Be(0, "the project manager is read only when the user has no role");

        // Another day: the project list is cached per date.
        fake.Projects = [];
        fake.Assignments = new TaskAssignments(
            false, new Dictionary<string, IReadOnlySet<string>>(), "Dana Whitfield", "dana.whitfield@example.com");
        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-25", CancellationToken.None));

        options.GetProperty("roles").GetArrayLength().Should().Be(0);
        options.GetProperty("no_role").GetString().Should()
            .Contain("no role on project P005678-001").And.Contain("Dana Whitfield (dana.whitfield@example.com)");
        options.GetProperty("next_step").GetString().Should().StartWith("Don't call save_timecard");
        options.GetProperty("tasks").GetArrayLength().Should().BeGreaterThan(0, "the task list still comes back");
    }

    [Fact]
    public async Task Options_WithoutRole_StillAnswerWhenTheProjectManagerCannotBeRead()
    {
        var (service, fake) = CreateService();
        fake.Projects = [];
        fake.AssignmentsException = new ProjectorApiException("No permission to view project.", "ViewPermissionDenied");

        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));

        options.GetProperty("no_role").GetString().Should().Contain("Ask the project manager to add you");
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
    public void ParseTimeCards_StatusRejected_ReturnsTheRejectedCardWithItsReason()
    {
        var doc = XDocument.Parse("""
            <Envelope><Body><PwsGetTimeCardsResult><TimeEntryProjects><PwsTimeEntryProject>
              <ProjectDescriptor><ProjectCode>P005678-001</ProjectCode><ProjectName>Contoso</ProjectName></ProjectDescriptor>
              <TimeCards>
                <PwsTimecardDetail>
                  <TimecardUid>900</TimecardUid><WorkDate>2026-09-25T00:00:00</WorkDate><WorkMinutes>30</WorkMinutes><CardStatus>S</CardStatus>
                </PwsTimecardDetail>
                <PwsTimecardDetail>
                  <TimecardUid>901</TimecardUid><RejectedReason>Wrong end customer</RejectedReason>
                  <WorkDate>2026-09-25T00:00:00</WorkDate><WorkMinutes>30</WorkMinutes><CardStatus>R</CardStatus>
                </PwsTimecardDetail>
              </TimeCards>
            </PwsTimeEntryProject></TimeEntryProjects></PwsGetTimeCardsResult></Body></Envelope>
            """);

        var card = ProjectorResponseParsers.ParseTimeCards(doc, status: "Rejected").Single();
        card.TimecardUid.Should().Be("901");
        card.Status.Should().Be("Rejected");
        card.RejectedReason.Should().Be("Wrong end customer");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("rejected", "Rejected")]
    [InlineData(" R ", "Rejected")]
    [InlineData("DRAFT", "Draft")]
    [InlineData("s", "Submitted")]
    public void ListTimecards_StatusFilter_IsNormalized(string? input, string? expected) =>
        ProjectorToolService.NormalizeCardStatus(input).Should().Be(expected);

    [Fact]
    public void ListTimecards_UnknownStatus_IsRefusedWithTheValidValues()
    {
        // "Reject" used to match nothing, and an empty list reads as "no rejected cards".
        var act = () => ProjectorToolService.NormalizeCardStatus("Reject");
        act.Should().Throw<ArgumentException>().WithMessage("*'Reject'*Draft, Submitted, Approved, Rejected*");
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
    public async Task Options_ListNoRateTypes_OnlyEachTasksDefault()
    {
        // Open tasks: Development (Billable) and Project Management (no task type: Billable, Non-Chargeable). The save
        // always uses the default, so the allowed lists are left out.
        var (service, _) = CreateService();
        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));

        options.TryGetProperty("rate_types", out _).Should().BeFalse();
        var tasks = options.GetProperty("tasks").EnumerateArray().ToList();
        tasks.Should().AllSatisfy(t => t.TryGetProperty("rate_types", out _).Should().BeFalse());
        tasks.Should().AllSatisfy(t => t.GetProperty("default_rate_type").GetString().Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task Save_WarnsOnNonWorkingHolidayAndPtoDays_ButKeepsTheCards()
    {
        var schedule = new FakeSchedule
        {
            Schedule = Schedule(
                [("2026-09-04", 480, 0), ("2026-09-07", 480, 0), ("2026-09-24", 480, 480), ("2026-10-03", 0, 0)],
                holidays: [("2026-09-07", "Labour Day")],
                pto: [("2026-09-04", 480)])
        };
        var (service, _) = CreateService(schedule: schedule);

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            [Input(date: "2026-10-03"), Input(date: "2026-09-07"), Input(date: "2026-09-04"), Input(date: "2026-09-24")],
            dryRun: true, CancellationToken.None));

        var cards = result.GetProperty("results").EnumerateArray().ToList();
        cards.Select(c => c.GetProperty("status").GetString()).Should().AllBe("valid", "a date warning never stops a card");
        Warnings(cards[0]).Should().ContainSingle().Which.Should().Contain("not a working day");
        Warnings(cards[1]).Should().ContainSingle().Which.Should().Contain("holiday").And.Contain("Labour Day");
        Warnings(cards[2]).Should().ContainSingle().Which.Should().Contain("8 h of PTO");
        Warnings(cards[3]).Should().BeEmpty();
        schedule.Calls.Should().ContainSingle("one schedule read for the whole batch").Which.Should().Be("2026-09-04..2026-10-03");
        result.GetProperty("days").EnumerateArray()
            .Single(d => d.GetProperty("work_date").GetString() == "2026-09-24")
            .GetProperty("expected_hours").GetDouble().Should().Be(8);
    }

    [Fact]
    public async Task Save_WarnsWhenTheDayGoesOverExpected()
    {
        var schedule = new FakeSchedule { Schedule = Schedule([("2026-09-24", 480, 480)]) };
        var (service, fake) = CreateService(schedule: schedule);
        fake.DayCards = [DayCard("800", minutes: 420, task: "2100000000000000099", narrative: "Workshop")];

        var result = Json(await service.SaveTimecardsAsync(ConnectionId, [Input(hours: 1.5)], dryRun: false, CancellationToken.None));

        var card = result.GetProperty("results")[0];
        card.GetProperty("status").GetString().Should().Be("saved");
        Warnings(card).Should().ContainSingle().Which.Should().Contain("8.5 h, more than the 8 h");
        card.GetProperty("day").GetProperty("expected_hours").GetDouble().Should().Be(8);
    }

    [Fact]
    public async Task Save_WarnsAfterTheRoleEnds_AndNearTheProjectClose()
    {
        var (service, fake) = CreateService(schedule: new FakeSchedule
        {
            Schedule = Schedule([("2026-09-24", 480, 480)], closeDates: [("P005678-001", "2026-09-27")])
        });

        var near = Json(await service.SaveTimecardsAsync(ConnectionId, [Input()], dryRun: true, CancellationToken.None));
        Warnings(near.GetProperty("results")[0]).Should().ContainSingle().Which.Should().Contain("project ends on 2026-09-27");

        var (after, afterFake) = CreateService(schedule: new FakeSchedule { Schedule = Schedule([("2026-09-24", 480, 480)]) });
        afterFake.Projects = fake.Projects.Select(p => WithRoleEnd(p, "2026-09-20")).ToList();
        var result = Json(await after.SaveTimecardsAsync(ConnectionId, [Input()], dryRun: true, CancellationToken.None));
        Warnings(result.GetProperty("results")[0]).Should().ContainSingle()
            .Which.Should().Contain("after the end of your role on this project (2026-09-20)");
    }

    [Fact]
    public async Task Save_WithoutTheSchedule_StillSaves_AndSaysTheDateChecksAreMissing()
    {
        var (service, fake) = CreateService(schedule: new FakeSchedule
        {
            Exception = new ProjectorApiException("Projector is down.", "projector_unavailable")
        });

        var result = Json(await service.SaveTimecardsAsync(ConnectionId, [Input()], dryRun: false, CancellationToken.None));

        result.GetProperty("saved_count").GetInt32().Should().Be(1);
        fake.Saves.Should().ContainSingle();
        result.GetProperty("warnings")[0].GetString().Should().Contain("schedule could not be read");
        result.GetProperty("days")[0].TryGetProperty("expected_hours", out _).Should().BeFalse();
    }

    private static List<string> Warnings(JsonElement card) =>
        card.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];

    private static TimeEntryProjectSummary WithRoleEnd(TimeEntryProjectSummary p, string end) => new()
    {
        ProjectCode = p.ProjectCode,
        ProjectUid = p.ProjectUid,
        ProjectName = p.ProjectName,
        EngagementCode = p.EngagementCode,
        EngagementName = p.EngagementName,
        ClientName = p.ClientName,
        Billable = p.Billable,
        LocationName = p.LocationName,
        UnavailableReasonCode = p.UnavailableReasonCode,
        Roles = p.Roles.Select(r => r with { EndDate = end }).ToList()
    };

    internal static ResourceSchedule Schedule(
        (string Date, int Normal, int Basis)[] dates,
        (string Date, string Name)[]? holidays = null,
        (string Date, int Minutes)[]? pto = null,
        (string ProjectCode, string Close)[]? closeDates = null) => new()
    {
        Dates = dates.Select(d => new ScheduleDate
        {
            Date = d.Date, NormalWorkingMinutes = d.Normal, UtilizationBasisMinutes = d.Basis
        }).ToList(),
        Holidays = (holidays ?? []).Select(h => new ScheduleHoliday { Date = h.Date, HolidayName = h.Name, TimeOffMinutes = 1440 }).ToList(),
        TimeOff = (pto ?? []).Select(t => new ScheduleTimeOff { Date = t.Date, TimeOffReason = "PTO", TimeOffMinutes = t.Minutes }).ToList(),
        Roles = (closeDates ?? []).Select(c => new ScheduleRole { ProjectCode = c.ProjectCode, ProjectCloseDate = c.Close }).ToList()
    };

    /// <summary>The caller's schedule for the save's date checks; records each read as start..end.</summary>
    private sealed class FakeSchedule : IProjectorScheduleClient
    {
        public ResourceSchedule Schedule { get; set; } = new();
        public Exception? Exception { get; set; }
        public List<string> Calls { get; } = [];

        public Task<ResourceSchedule> GetResourceScheduleAsync(
            ProjectorConnection connection, string? resourceReferenceSystemId, string startDate, string endDate,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{startDate}..{endDate}");
            return Exception is null ? Task.FromResult(Schedule) : Task.FromException<ResourceSchedule>(Exception);
        }

        public Task<Projector.Domain.Availability.AvailabilitySummary> CheckAvailabilityAsync(
            ProjectorConnection connection, string? resourceReferenceSystemId, string startDate, string endDate,
            string? displayName = null, string? emailAddress = null, double requiredMinutesPerWeek = 0,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Projector.Domain.Holidays.HolidayEntry>> GetResourcePtoHolidaysAsync(
            ProjectorConnection connection, string resourceReferenceSystemId, string cutoffDate,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Projector.Domain.Engagements.UtilizationYear>> GetUtilizationAsync(
            ProjectorConnection connection, string resourceReferenceSystemId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
    public async Task Save_OtherTaskIsNotASameTaskDuplicate_AndUpdateIsNoDuplicate()
    {
        var (service, fake) = CreateService();
        fake.DayCards = [DayCard("802", 90, "2100000000000000002", "Fix build pipeline")];
        var other = Json(await service.SaveTimecardAsync(ConnectionId, Input(), CancellationToken.None));
        other.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .ContainSingle("the existing card is on another task: only the other-task warning (J4, v0.6.4)")
            .Which.Should().StartWith("Possible duplicate on another task: card 802");

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

        var all = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None, chargeableOnly: false));
        var chargeable = all.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("chargeable").GetBoolean()).ToList();
        chargeable.Should().Equal(true, false);

        var page = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 1, CancellationToken.None, offset: 1, chargeableOnly: false));
        page.GetProperty("projects")[0].GetProperty("chargeable").GetBoolean().Should().BeFalse();
        page.GetProperty("has_more").GetBoolean().Should().BeFalse();

        var filtered = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "contoso", 50, CancellationToken.None));
        filtered.GetProperty("total").GetInt32().Should().Be(1);

        fake.Calls["projects"].Should().Be(1, "query and paging filter the cached list");
    }

    // ---- Tasks Projector auto-rejects at submit, card reads, call limit (v0.6.0, 2026-09-28) ----

    [Fact]
    public async Task Options_LeaveOutSummaryTasks_AndCountThem()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents();

        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));

        options.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("wbs_code").GetString())
            .Should().Equal("1.1", "1.2", "2.1", "2.2");
        options.GetProperty("tasks_summary_hidden").GetInt32().Should().Be(2);
        options.TryGetProperty("assignment_note", out _).Should().BeFalse();
        fake.Calls["assignments"].Should().Be(0, "only projects with AllowAssignmentFlag=false need the assignments");
    }

    [Fact]
    public async Task Save_SummaryTask_IsRefusedWithItsSubTasks()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents();

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(task: "1"), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("summary_task");
        error.Message.Should().Contain("Nothing was saved")
            .And.Contain("User Story 101: Export totals do not match > Analysis & Design (WBS 1.1)")
            .And.Contain("> Development (WBS 1.2)");
        fake.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Options_RestrictedProject_MarksAssignedTasks_AndCachesTheAssignments()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents(allowAssignment: false);
        fake.Assignments = Assignments(restricted: true, ("2100000000000000012", "2200000000000000001"));

        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));
        await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None, query: "202");

        var assigned = options.GetProperty("tasks").EnumerateArray()
            .ToDictionary(t => t.GetProperty("wbs_code").GetString()!, t => t.GetProperty("assigned").GetBoolean());
        assigned.Should().Equal(new Dictionary<string, bool> { ["1.1"] = true, ["1.2"] = false, ["2.1"] = false, ["2.2"] = false });
        options.GetProperty("assignment_note").GetString().Should().Contain("assigned = false");
        fake.Calls["assignments"].Should().Be(1, "assignments are cached per user and project");
    }

    [Fact]
    public async Task Save_NotAssignedTask_IsRefused_AssignedTaskIsSaved()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents(allowAssignment: false);
        fake.Assignments = Assignments(restricted: true, ("2100000000000000012", "2200000000000000001"));

        var act = () => service.SaveTimecardAsync(ConnectionId, Input(task: "1.2"), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("not_assigned_to_task");
        error.Message.Should().Contain("Nothing was saved").And.Contain("project manager");
        fake.SaveCalls.Should().Be(0);

        await service.SaveTimecardAsync(ConnectionId, Input(task: "1.1"), CancellationToken.None);
        fake.Saves.Should().ContainSingle().Which.TaskUid.Should().Be("2100000000000000012");
    }

    [Fact]
    public async Task FailedAssignmentRead_NeverBlocks_AndSaysSo()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents(allowAssignment: false);
        fake.AssignmentsException = new ProjectorApiException("No permission to view project.", "NoPermission");

        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None));
        options.GetProperty("tasks").EnumerateArray().Should().AllSatisfy(t => t.TryGetProperty("assigned", out _).Should().BeFalse());
        options.GetProperty("assignment_note").GetString().Should().Contain("could not be read");

        await service.SaveTimecardAsync(ConnectionId, Input(task: "1.2"), CancellationToken.None);
        fake.Saves.Should().ContainSingle();
    }

    [Fact]
    public void ParseTaskAssignments_ReadsFlagAndRolesPerTask()
    {
        var doc = XDocument.Parse(
            "<Envelope><Body><R><TimeEntryRestrictedToRolesAssignedToTasksFlag>true</TimeEntryRestrictedToRolesAssignedToTasksFlag>" +
            Detail("t1", "r1") + Detail("t1", "r2") + Detail("t2", "r2") +
            "<Manager><UserDisplayName>Dana Whitfield</UserDisplayName><EmailAddress>dana.whitfield@example.com</EmailAddress></Manager>" +
            "<EngagementManager><UserDisplayName>Someone Else</UserDisplayName></EngagementManager>" +
            "</R></Body></Envelope>");

        var assignments = ProjectorTimeEntryParsers.ParseTaskAssignments(doc);

        assignments.ManagerName.Should().Be("Dana Whitfield");
        assignments.ManagerEmail.Should().Be("dana.whitfield@example.com");
        assignments.Restricted.Should().BeTrue();
        assignments.IsAssigned("t1", ["r1"]).Should().BeTrue();
        assignments.IsAssigned("t2", ["r1"]).Should().BeFalse();
        assignments.IsAssigned("t3", ["r1", "r2"]).Should().BeFalse();

        static string Detail(string task, string role) =>
            $"<ProjectTaskRoleDetail><ProjectRoleIdentity><ProjectRoleUid>{role}</ProjectRoleUid></ProjectRoleIdentity>" +
            $"<ProjectTaskIdentity><ProjectTaskUid>{task}</ProjectTaskUid></ProjectTaskIdentity></ProjectTaskRoleDetail>";
    }

    [Theory]
    [InlineData("ACE", "ACE Consulting Group", true)]
    [InlineData("ACE", "Workplace", false)]
    [InlineData("ace consult", "ACE Consulting Group", true)]
    [InlineData("3.4", "3.4.2", true)]
    [InlineData("3.4", "4.3", false)]
    [InlineData("P005678", "P005678-001", true)]
    [InlineData("Design", "User Story 101 > Analysis & Design", true)]
    [InlineData("sign", "User Story 101 > Analysis & Design", false)]
    public void TextMatch_MatchesWholeWordsOrWordStarts(string query, string value, bool expected) =>
        TextMatch.Matches(query, value).Should().Be(expected);

    [Fact]
    public void TextMatch_RanksExactThenPrefixThenWord()
    {
        TextMatch.Score("ACE", "ACE").Should().Be(3);
        TextMatch.Score("ACE", "ACE Consulting").Should().Be(2);
        TextMatch.Score("Consulting", "ACE Consulting").Should().Be(1);
    }

    [Fact]
    public void GetTimeCardsEnvelope_AsksForEveryStatus_AndOnlyTheReferencedTasks()
    {
        var xml = XDocument.Parse(ProjectorEnvelopeBuilders.BuildGetTimeCards("ticket", null, "2026-09-21", "2026-09-25"));
        var request = xml.Descendants().Single(e => e.Name.LocalName == "serviceRequest");

        request.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "SessionTicket", "EndDate", "IncludeApprovedFlag", "IncludeDraftFlag", "IncludeReferencedTasksOnlyFlag",
            "IncludeRejectedFlag", "IncludeSubmittedFlag", "IncludeTimeCardsFlag", "IncludeTimeOffCardsFlag", "StartDate");
        request.Elements().Where(e => e.Name.LocalName.StartsWith("Include", StringComparison.Ordinal)
                && e.Name.LocalName != "IncludeTimeOffCardsFlag")
            .Should().AllSatisfy(e => e.Value.Should().Be("true"));
    }

    [Fact]
    public void TaskPaths_StartAtTheParentName_WhenTheParentIsNotInTheResponse()
    {
        var paths = TaskPaths.Build(
        [
            ("10", "Development", "1", "User Story 101"),
            ("20", "Story", null, null),
            ("21", "Analysis", "20", "Story"),
        ]);

        paths["10"].Should().Be("User Story 101 > Development");
        paths["21"].Should().Be("Story > Analysis");
    }

    [Fact]
    public async Task CallLimiter_LetsThreeCallsPerUserRun_TheFourthWaits_OtherUsersDoNot()
    {
        var limiter = new ProjectorCallLimiter();
        var slots = new List<IDisposable>();
        for (var i = 0; i < ProjectorCallLimiter.MaxConcurrentPerUser; i++)
        {
            slots.Add(await limiter.EnterAsync("user-a", CancellationToken.None));
        }

        var fourth = limiter.EnterAsync("user-a", CancellationToken.None);
        var otherUser = limiter.EnterAsync("user-b", CancellationToken.None);

        otherUser.IsCompleted.Should().BeTrue();
        fourth.IsCompleted.Should().BeFalse();

        slots[0].Dispose();
        (await fourth.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        (await otherUser).Dispose();
        slots.Skip(1).ToList().ForEach(s => s.Dispose());
        limiter.ActiveUsers.Should().Be(0, "idle users are removed");
    }

    [Fact]
    public async Task BusyRead_IsRetriedOnce()
    {
        ProjectorSoapHttp.BusyRetryDelay = TimeSpan.Zero;
        var handler = new ScriptedHandler(BusyResponse, OkResponse);
        var soap = new ProjectorSoapHttp(new HttpClient(handler), NullLogger<ProjectorSoapHttp>.Instance, new ProjectorCallLimiter());

        await soap.PostWcfAsync(Connection(), "PwsGetTimeCards", new XElement("x"));

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task BusyRead_TwiceInARow_BecomesProjectorBusy()
    {
        ProjectorSoapHttp.BusyRetryDelay = TimeSpan.Zero;
        var handler = new ScriptedHandler(BusyResponse, BusyResponse);
        var soap = new ProjectorSoapHttp(new HttpClient(handler), NullLogger<ProjectorSoapHttp>.Instance, new ProjectorCallLimiter());

        var act = () => soap.PostWcfAsync(Connection(), "PwsGetTimeCards", new XElement("x"));

        (await act.Should().ThrowAsync<ProjectorApiException>()).Which.ErrorCode.Should().Be("projector_busy");
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task BusySave_IsNeverRetried()
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") });
        var write = new ProjectorSoapWriteHttp(new HttpClient(handler), NullLogger<ProjectorSoapHttp>.Instance, new ProjectorCallLimiter());

        var act = () => write.Soap.PostWcfAsync(Connection(), "PwsSaveTimeCards", new XElement("x"));

        var error = (await act.Should().ThrowAsync<ProjectorApiException>()).Which;
        error.ErrorCode.Should().Be("projector_busy");
        error.Message.Should().StartWith("Nothing was saved");
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void ResolveTask_AcceptsTheTailOfTheFullPath_AsListTimecardsShowsIt()
    {
        TimeEntryTask Task(string uid, string path, string wbs) => new() { Uid = uid, Name = path.Split(" > ")[^1], Path = path, WbsCode = wbs, OpenForTime = true };
        var tasks = new List<TimeEntryTask>
        {
            Task("1", "Build > Validation > Auth flow > Validate sign-in", "3.2.1.1"),
            Task("2", "Build > Validation > Claims > Validate sign-in", "3.2.2.1"),
        };

        TimeEntryToolService.ResolveTask(tasks, "Auth flow > Validate sign-in", "P005678-001").Uid.Should().Be("1");
        TimeEntryToolService.ResolveTask(tasks, "Validation > Claims > Validate sign-in", "P005678-001").Uid.Should().Be("2");

        var act = () => TimeEntryToolService.ResolveTask(tasks, "Validate sign-in", "P005678-001");
        act.Should().Throw<ProjectorApiException>().Which.ErrorCode.Should().Be("ambiguous_task");
    }

    [Fact]
    public void RefusedToolCall_IsLoggedWithCodeAndReason()
    {
        var logger = new ListLogger();

        var result = Projector.Mcp.Server.Tools.AgentTools.ToError(
            new ProjectorApiException("Task 'X' is a summary task.", "summary_task"), logger);

        result.IsError.Should().BeTrue();
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("summary_task") && e.Message.Contains("is a summary task"));
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void ToolArguments_NullsAreDropped_WholeNumbersBind_BadValuesAreNamed()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "max_rows":{"type":"integer"},"hours":{"type":"number"},"flag":{"type":"boolean"},"query":{"type":["string","null"]}}}
            """).RootElement;
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

        var args = new Dictionary<string, JsonElement> { ["max_rows"] = J("20.0"), ["query"] = J("null"), ["hours"] = J("\"1.5\""), ["flag"] = J("\"true\"") };
        Projector.Mcp.Server.Tools.ToolArgumentFilter.Normalize(args, schema).Should().BeNull();
        args.Should().NotContainKey("query");
        args["max_rows"].GetInt32().Should().Be(20);
        args["hours"].GetDouble().Should().Be(1.5);
        args["flag"].GetBoolean().Should().BeTrue();

        Projector.Mcp.Server.Tools.ToolArgumentFilter.Normalize(new Dictionary<string, JsonElement> { ["max_rows"] = J("2.5") }, schema)
            .Should().Contain("max_rows must be a whole number");
    }

    [Fact]
    public async Task Options_QueryWithNoMatch_SaysWhatToTryNext()
    {
        var (service, _) = CreateService(tree: true);

        var options = Json(await service.GetTimecardOptionsAsync(ConnectionId, "P005678-001", "2026-09-24", CancellationToken.None, query: "zzqq"));

        options.GetProperty("tasks_total").GetInt32().Should().Be(0);
        options.GetProperty("next_step").GetString().Should().Contain("No task matches 'zzqq'")
            .And.Contain("WBS").And.Contain("list_timecards with project_code and query");
    }

    [Fact]
    public async Task ListTimeProjects_ShowsChargeableOnlyByDefault()
    {
        var (service, fake) = CreateService();
        fake.Projects.Add(new TimeEntryProjectSummary { ProjectCode = "P009999-001", ProjectName = "Fabrikam Rollout" });

        var byDefault = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None));
        byDefault.GetProperty("projects").EnumerateArray().Should().AllSatisfy(p => p.GetProperty("chargeable").GetBoolean().Should().BeTrue());
        byDefault.GetProperty("not_chargeable_hidden").GetInt32().Should().BeGreaterThan(0);

        var all = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None, chargeableOnly: false));
        all.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("project_code").GetString()).Should().Contain("P009999-001");
        all.TryGetProperty("not_chargeable_hidden", out _).Should().BeFalse();

        var hidden = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "Fabrikam", 50, CancellationToken.None));
        hidden.GetProperty("total").GetInt32().Should().Be(0);
        hidden.GetProperty("next_step").GetString().Should().Contain("no role").And.Contain("chargeable_only = false");
        fake.Calls["projects"].Should().Be(1, "all three answers come from the cached list");
    }

    // ---- Batch save (v0.6.2) ------------------------------------------------------------------

    private static List<SaveTimecardInput> Batch(params (string Task, double Hours, string Narrative)[] cards) =>
        cards.Select(c => Input(task: c.Task, hours: c.Hours, narrative: c.Narrative)).ToList();

    [Fact]
    public async Task Batch_SavesTheValidCards_AndReportsTheInvalidOnes()
    {
        var (service, fake) = CreateService(tree: true);
        fake.Setup = TreeWithOpenParents(allowAssignment: false);
        fake.Assignments = Assignments(restricted: true, ("2100000000000000012", "2200000000000000001"));
        fake.DayCards = [DayCard("800", minutes: 60, task: "2100000000000000099", narrative: "Weekly status call")];

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1.5, "Export analysis"), ("1", 1, "Summary task work"), ("1.2", 0.5, "Unassigned work")),
            dryRun: false, CancellationToken.None));

        result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .Should().Equal("saved", "invalid", "invalid");
        result.GetProperty("results")[1].GetProperty("error").GetString().Should().Be("summary_task");
        result.GetProperty("results")[2].GetProperty("error").GetString().Should().Be("not_assigned_to_task");
        result.GetProperty("saved_count").GetInt32().Should().Be(1);
        result.GetProperty("invalid_count").GetInt32().Should().Be(2);
        fake.Saves.Should().ContainSingle().Which.TaskUid.Should().Be("2100000000000000012");
        result.GetProperty("days")[0].GetProperty("total_hours").GetDouble().Should().Be(2.5);
    }

    [Fact]
    public async Task Batch_DryRun_SavesNothing_AndProjectsTheDayTotal()
    {
        var (service, fake) = CreateService(tree: true);
        fake.DayCards = [DayCard("800", minutes: 60, task: "2100000000000000099", narrative: "Weekly status call")];

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1.5, "Export analysis"), ("2.2", 0.5, "Invoice layout fix")),
            dryRun: true, CancellationToken.None));

        fake.SaveCalls.Should().Be(0);
        result.GetProperty("action").GetString().Should().Be("dry_run");
        result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .Should().Equal("valid", "valid");
        result.GetProperty("valid_count").GetInt32().Should().Be(2);
        result.GetProperty("days")[0].GetProperty("total_hours").GetDouble().Should().Be(3);
        result.GetProperty("days")[0].GetProperty("card_count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Batch_UnknownOutcome_StopsBeforeTheNextCard()
    {
        var (service, fake) = CreateService(tree: true);
        fake.SaveException = new ProjectorApiException("Projector did not answer.", "write_outcome_unknown");
        fake.SaveExceptionOnCall = 2;

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1, "First"), ("1.2", 1, "Second"), ("2.1", 1, "Third")),
            dryRun: false, CancellationToken.None));

        result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .Should().Equal("saved", "failed", "not_attempted");
        result.GetProperty("results")[1].GetProperty("error").GetString().Should().Be("write_outcome_unknown");
        fake.SaveCalls.Should().Be(2, "a card after an unknown outcome is never sent");
    }

    [Fact]
    public async Task Batch_ProjectorErrorOnOneCard_DoesNotStopTheOthers()
    {
        var (service, fake) = CreateService(tree: true);
        fake.SaveException = new ProjectorApiException("Accounting period closed.", "CannotChangeSubmittedTimecardsApClosed");
        fake.SaveExceptionOnCall = 1;

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1, "First"), ("1.2", 1, "Second")),
            dryRun: false, CancellationToken.None));

        result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .Should().Equal("failed", "saved");
        result.GetProperty("results")[0].GetProperty("message").GetString().Should().Contain("accounting period");
    }

    [Fact]
    public async Task Batch_WarnsAboutADuplicateInsideTheSameCall()
    {
        var (service, fake) = CreateService(tree: true);

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1, "Export totals analysis with the customer"), ("1.1", 1, "Export totals analysis with customer")),
            dryRun: false, CancellationToken.None));

        result.GetProperty("results")[0].TryGetProperty("warnings", out _).Should().BeFalse();
        result.GetProperty("results")[1].GetProperty("warnings")[0].GetString().Should().Contain("Possible duplicate");
        fake.SaveCalls.Should().Be(2, "a duplicate is a warning, not a refusal");
    }

    [Fact]
    public async Task Batch_TooManyCards_IsRefused()
    {
        var (service, fake) = CreateService(tree: true);
        var cards = Enumerable.Range(0, TimeEntryToolService.MaxCardsPerSave + 1).Select(_ => Input(task: "1.1")).ToList();

        var act = () => service.SaveTimecardsAsync(ConnectionId, cards, dryRun: false, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.SaveCalls.Should().Be(0);
    }

    [Fact]
    public void ToolArguments_NormalizeInsideCards()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"cards":{"type":"array","items":{"type":"object","properties":{
              "hours":{"type":"number"},"location":{"type":["string","null"]}}}}}}
            """).RootElement;
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

        var args = new Dictionary<string, JsonElement> { ["cards"] = J("""[{"hours":"0.25","location":null}]""") };
        Projector.Mcp.Server.Tools.ToolArgumentFilter.Normalize(args, schema).Should().BeNull();
        var card = args["cards"][0];
        card.GetProperty("hours").GetDouble().Should().Be(0.25);
        card.TryGetProperty("location", out _).Should().BeFalse();

        var asString = new Dictionary<string, JsonElement> { ["cards"] = J("\"[{\\\"hours\\\":1}]\"") };
        Projector.Mcp.Server.Tools.ToolArgumentFilter.Normalize(asString, schema).Should().BeNull();
        asString["cards"].GetArrayLength().Should().Be(1);

        Projector.Mcp.Server.Tools.ToolArgumentFilter.Normalize(
                new Dictionary<string, JsonElement> { ["cards"] = J("""[{"hours":1},{"hours":"lots"}]""") }, schema)
            .Should().Be("cards[1].hours must be a number (got \"lots\").");
    }

    // ---- Recent usage and task search in list_time_projects (v0.6.2) ---------------------------------

    private static Timecard RecentCard(
        string project, string date, int minutes, string taskUid, string path, string wbs, string? description = null) => new()
    {
        Description = description,
        ProjectCode = project,
        WorkDate = date,
        WorkMinutes = minutes,
        WorkHours = minutes / 60.0,
        ProjectTaskUid = taskUid,
        TaskName = path.Split(" > ")[^1],
        TaskPath = path,
        TaskWbsCode = wbs,
        CardStatusCode = "S"
    };

    private static (TimeEntryToolService Service, FakeTimeEntryClient Fake) CreateServiceWithRecentUse()
    {
        var (service, fake) = CreateService();
        fake.Projects.Add(new TimeEntryProjectSummary
        {
            ProjectCode = "P007777-001",
            ProjectName = "Northwind - Presale",
            Roles = [new TimeEntryRole("2200000000000000077", "Architect", null, null)]
        });
        fake.RecentCards =
        [
            RecentCard("P005678-001", "2026-09-10", 60, "t1", "Build > Development", "1.2"),
            RecentCard("P007777-001", "2026-09-22", 30, "t7", "Presale > Tailspin onboarding", "185"),
            RecentCard("P007777-001", "2026-09-23", 45, "t7", "Presale > Tailspin onboarding", "185"),
            RecentCard("P007777-001", "2026-09-15", 60, "t8", "Presale > Proposal writing", "12")
        ];
        return (service, fake);
    }

    [Fact]
    public async Task ListTimeProjects_ShowsRecentUse_MostRecentFirst()
    {
        var (service, fake) = CreateServiceWithRecentUse();

        var result = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None));

        var projects = result.GetProperty("projects").EnumerateArray().ToList();
        projects.Select(p => p.GetProperty("project_code").GetString()).Should().Equal("P007777-001", "P005678-001");
        projects[0].GetProperty("last_used").GetString().Should().Be("2026-09-23");
        projects[0].GetProperty("hours_last_30d").GetDouble().Should().Be(2.25);
        var tasks = projects[0].GetProperty("recent_tasks").EnumerateArray().ToList();
        tasks.Select(t => t.GetProperty("wbs_code").GetString()).Should().Equal("185", "12");
        tasks[0].GetProperty("hours").GetDouble().Should().Be(1.25);
        result.TryGetProperty("recent_note", out _).Should().BeFalse();

        await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "presale", 50, CancellationToken.None);
        fake.Calls["recent"].Should().Be(1, "recent usage is cached per user and date");
    }

    [Fact]
    public async Task ListTimeProjects_QueryFindsTheProjectThroughARecentTask()
    {
        var (service, _) = CreateServiceWithRecentUse();

        var result = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "Tailspin", 50, CancellationToken.None));

        var project = result.GetProperty("projects").EnumerateArray().Should().ContainSingle().Subject;
        project.GetProperty("project_code").GetString().Should().Be("P007777-001");
        project.GetProperty("matched_tasks")[0].GetProperty("task_path").GetString().Should().Be("Presale > Tailspin onboarding");
        project.GetProperty("matched_tasks")[0].GetProperty("wbs_code").GetString().Should().Be("185");
    }

    [Fact]
    public async Task ListTimeProjects_QueryAlsoMatchesRecentCardDescriptions()
    {
        var (service, fake) = CreateServiceWithRecentUse();
        fake.RecentCards.Add(RecentCard(
            "P007777-001", "2026-09-21", 30, "t9", "Presale > Miscellaneous", "186", "[Litware] follow-up on the demo environment"));

        var result = Json(await service.ListTimeProjectsAsync(ConnectionId, "2026-09-24", "Litware", 50, CancellationToken.None));

        var project = result.GetProperty("projects").EnumerateArray().Should().ContainSingle().Subject;
        project.GetProperty("matched_tasks")[0].GetProperty("wbs_code").GetString().Should().Be("186");
        project.GetProperty("matched_tasks")[0].TryGetProperty("descriptions", out _).Should().BeFalse("descriptions are matched, not shown");
    }

    [Fact]
    public async Task ListTimeProjects_StillWorks_WhenRecentCardsCannotBeRead()
    {
        var (service, fake) = CreateService();
        fake.DayCardsException = null;
        var failing = new FailingRecentClient(fake);
        var store = new InMemoryProjectorConnectionStore();
        store.Save(Connection());
        var svc = new TimeEntryToolService(
            new ProjectorConnectionService(store, new NoRefreshTokenClient()), failing, new TimeEntryCache(),
            NullLogger<TimeEntryToolService>.Instance);

        var result = Json(await svc.ListTimeProjectsAsync(ConnectionId, "2026-09-24", null, 50, CancellationToken.None));

        result.GetProperty("projects").GetArrayLength().Should().BeGreaterThan(0);
        result.GetProperty("recent_note").GetString().Should().Contain("could not be read");
    }

    /// <summary>The fake client, except that the recent-cards read fails.</summary>
    private sealed class FailingRecentClient(FakeTimeEntryClient inner) : IProjectorTimeEntryClient
    {
        public Task<IReadOnlyList<TimeEntryProjectSummary>> SearchTimeEntryProjectsAsync(
            ProjectorConnection connection, string workDate, string? query = null, string? projectCode = null,
            CancellationToken cancellationToken = default) =>
            inner.SearchTimeEntryProjectsAsync(connection, workDate, query, projectCode, cancellationToken);

        public Task<TimeEntryProjectSetup?> GetTimeEntryProjectAsync(
            ProjectorConnection connection, string projectCode, string workDate, CancellationToken cancellationToken = default) =>
            inner.GetTimeEntryProjectAsync(connection, projectCode, workDate, cancellationToken);

        public Task<TimeEntryParameters> GetTimeEntryParametersAsync(ProjectorConnection connection, CancellationToken cancellationToken = default) =>
            inner.GetTimeEntryParametersAsync(connection, cancellationToken);

        public Task<TaskAssignments> GetTaskAssignmentsAsync(
            ProjectorConnection connection, string projectCode, CancellationToken cancellationToken = default) =>
            inner.GetTaskAssignmentsAsync(connection, projectCode, cancellationToken);

        public Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
            ProjectorConnection connection, string workDate, CancellationToken cancellationToken = default) =>
            inner.ListOwnTimecardsAsync(connection, workDate, cancellationToken);

        public Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
            ProjectorConnection connection, string startDate, string endDate, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<Timecard>>(new ProjectorApiException("Timeout.", "RequestTimeout"));

        public Task<OwnTimecard?> GetOwnTimecardAsync(
            ProjectorConnection connection, string timecardUid, string workDate, CancellationToken cancellationToken = default) =>
            inner.GetOwnTimecardAsync(connection, timecardUid, workDate, cancellationToken);

        public Task<TimecardSaveResult> SaveTimecardAsync(
            ProjectorConnection connection, TimecardSaveRequest request, CancellationToken cancellationToken = default) =>
            inner.SaveTimecardAsync(connection, request, cancellationToken);
    }

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><R/></s:Body></s:Envelope>")
    };

    private static HttpResponseMessage BusyResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><R><Messages><PwsMessage>" +
            "<ErrorCode>TooManyRequests</ErrorCode><ErrorText>You currently have 5 active requests, exceeding the threshold of 4.</ErrorText>" +
            "</PwsMessage></Messages></R></s:Body></s:Envelope>")
    };

    private static TaskAssignments Assignments(bool restricted, params (string Task, string Role)[] pairs) =>
        new(restricted, pairs.GroupBy(p => p.Task).ToDictionary(
            g => g.Key, g => (IReadOnlySet<string>)g.Select(p => p.Role).ToHashSet(), StringComparer.Ordinal));

    /// <summary>Answers each call with the next response factory; the last one repeats.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]());
    }

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result,
        new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private static Timecard DayCard(string uid, int minutes, string task, string narrative, (string Uid, string Name)? role = null) => new()
    {
        ProjectRoleUid = role?.Uid,
        RoleName = role?.Name,
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

    // ---- v0.6.4: logs, long reads, duplicates across tasks, role check ----------------------

    [Theory]
    [InlineData("http://projectorpsa.com/PwsProjectorServices/IPwsProjectorServices/PwsGetEngagementList", "PwsGetEngagementList")]
    [InlineData("\"http://projectorpsa.com/OpsProjectorSvc/PwsGetTimeCards\"", "PwsGetTimeCards")]
    [InlineData("PwsSaveTimeCards", "PwsSaveTimeCards")]
    public void SoapActionName_IsTheMethodName(string header, string expected) =>
        ProjectorSoapHttp.SoapActionName(header).Should().Be(expected);

    [Fact]
    public void LongRead_OnlyTheEngagementList_GetsTheLongAttempt()
    {
        HttpRequestMessage Request(string method)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/PwsProjectorServices.svc");
            request.Headers.TryAddWithoutValidation("SOAPAction", SoapNamespaces.WcfSoapActionPrefix + method);
            return request;
        }

        var standard = TimeSpan.FromSeconds(10);
        ApiClientServiceCollectionExtensions.AttemptTimeoutFor(Request("PwsGetEngagementList"), standard)
            .Should().Be(TimeSpan.FromSeconds(25));
        ApiClientServiceCollectionExtensions.AttemptTimeoutFor(Request("PwsGetResourceList"), standard).Should().Be(standard);
        ApiClientServiceCollectionExtensions.AttemptTimeoutFor(null, standard).Should().Be(standard);
    }

    [Fact]
    public async Task LongRead_IsSentOnce_WithoutRetry()
    {
        // Registered the same way as production; only the primary handler is swapped for a failing counter.
        var handler = new CountingHandler(new HttpRequestException("connection reset"));
        var services = new ServiceCollection().AddLogging();
        services.AddProjectorApiClient();
        services.AddHttpClient<ProjectorSoapHttp>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var soap = provider.GetRequiredService<ProjectorSoapHttp>();

        var act = () => soap.PostWcfAsync(Connection(), "PwsGetEngagementList", new XElement("x"));

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Be(1, "one long attempt replaces 3 × 10 s for the engagement list");
    }

    [Fact]
    public async Task SoapFailure_IsLogged_WithTheActionAndElapsedTime()
    {
        var logger = new ListLogger<ProjectorSoapHttp>();
        var soap = new ProjectorSoapHttp(
            new HttpClient(new CountingHandler(new HttpRequestException("connection reset"))), logger);

        var act = () => soap.PostWcfAsync(Connection(), "PwsGetResourceList", new XElement("x"));

        await act.Should().ThrowAsync<HttpRequestException>();
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.StartsWith("PWS PwsGetResourceList failed after ", StringComparison.Ordinal)
            && e.Message.Contains("HttpRequestException"));
    }

    [Fact]
    public async Task Batch_WarnsAboutTheSameWorkOnAnotherTask_ButStillSaves()
    {
        // J4 (2026-09-28): the same coordination card was entered under two tasks of different projects.
        var (service, fake) = CreateService(tree: true);
        fake.DayCards =
        [
            DayCard("800", minutes: 30, task: "2100000000000000099",
                narrative: "IT migration internal coordination: answered questions on migration scope and licensing")
        ];

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 0.5, "IT migration internal coordination: answered questions on migration scope and licensing split")),
            dryRun: false, CancellationToken.None));

        fake.SaveCalls.Should().Be(1);
        result.GetProperty("results")[0].GetProperty("warnings")[0].GetString()
            .Should().Contain("Possible duplicate on another task: card 800");
    }

    [Fact]
    public void SimilarOnOtherTask_IgnoresTheSameTask_AndOtherWork()
    {
        var cards = new[]
        {
            DayCard("1", 60, "task-a", "Weekly status call with the customer team"),
            DayCard("2", 60, "task-b", "Invoice layout fix")
        };

        TimeEntryToolService.FindSimilarOnOtherTask(cards, "P005678-001", "task-a", "Weekly status call with customer team")
            .Should().BeNull("the same task is the ordinary duplicate check");
        TimeEntryToolService.FindSimilarOnOtherTask(cards, "P005678-001", "task-c", "Weekly status call with customer team")
            !.TimecardUid.Should().Be("1");
        TimeEntryToolService.FindSimilarOnOtherTask(cards, "P005678-001", "task-c", "Export totals analysis")
            .Should().BeNull();
    }

    [Fact]
    public void RoleWarning_OnlyWhenEarlierCardsOnTheProjectUseAnotherRole()
    {
        Timecard Card(string project, string roleUid, string roleName) => new()
        {
            ProjectCode = project, ProjectRoleUid = roleUid, RoleName = roleName, WorkDate = "2026-09-24"
        };

        var twoAsArchitect = new[] { Card("P1", "r-arch", "Architect"), Card("P1", "r-arch", "Architect"), Card("P2", "r-dev", "Developer") };
        TimeEntryToolService.RoleWarning("P1", "r-dev", "Developer", twoAsArchitect)
            .Should().Contain("role 'Developer'").And.Contain("use 'Architect'");
        TimeEntryToolService.RoleWarning("P1", "r-arch", "Architect", twoAsArchitect).Should().BeNull();
        TimeEntryToolService.RoleWarning("P2", "r-arch", "Architect", twoAsArchitect)
            .Should().BeNull("one earlier card is not a pattern");
        TimeEntryToolService.RoleWarning("P3", "r-arch", "Architect", twoAsArchitect).Should().BeNull();
        TimeEntryToolService.RoleWarning("P1", "r-dev", "Developer", [.. twoAsArchitect, Card("P1", "r-dev", "Developer")])
            .Should().BeNull("the user has used this role on the project before");
    }

    [Fact]
    public async Task Batch_WarnsWhenTheRoleDiffersFromEarlierCardsOnTheProject()
    {
        var (service, fake) = CreateService(tree: true);
        fake.DayCards =
        [
            DayCard("800", 60, "2100000000000000099", "Weekly status call", role: ("2200000000000000099", "Architect")),
            DayCard("801", 30, "2100000000000000098", "Design review", role: ("2200000000000000099", "Architect"))
        ];

        var result = Json(await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1, "Export analysis")), dryRun: true, CancellationToken.None));

        result.GetProperty("results")[0].GetProperty("warnings")[0].GetString()
            .Should().Contain("Role check").And.Contain("'System Engineer'").And.Contain("'Architect'");
    }

    [Fact]
    public async Task Batch_WritesOneAuditLine_WithCountsAndCodes_ButNoNarratives()
    {
        var logger = new ListLogger<TimeEntryToolService>();
        var (service, fake) = CreateService(tree: true, logger);
        fake.Setup = TreeWithOpenParents(allowAssignment: false);
        fake.Assignments = Assignments(restricted: true, ("2100000000000000012", "2200000000000000001"));

        await service.SaveTimecardsAsync(ConnectionId,
            Batch(("1.1", 1.5, "Secret customer narrative"), ("1", 1, "Summary task work"), ("1.2", 0.5, "Unassigned work")),
            dryRun: false, CancellationToken.None);

        var audit = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith("save_timecard audit:", StringComparison.Ordinal))
            .Which;
        audit.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Information);
        audit.Message.Should().Contain("save 3 card(s): 1 saved, 0 valid, 2 invalid, 0 failed, 0 not attempted")
            .And.Contain("errors [not_assigned_to_task,summary_task]")
            .And.Contain("projects [P005678-001]")
            .And.Contain("days [2026-09-24=1.5h/1]")
            .And.NotContain("Secret customer narrative");
    }

    [Fact]
    public void NoSaveToolHint_IsInTheTimeEntryPrompts()
    {
        Projector.Mcp.Server.Prompts.ProjectorPrompts.DraftDayTimecards().Text.Should().EndWith(TimeEntryToolService.NoSaveToolHint);
        Projector.Mcp.Server.Prompts.ProjectorPrompts.LogTime("2026-09-24", 1, "P005678-001", "Work").Text
            .Should().Contain(TimeEntryToolService.NoSaveToolHint);
    }

    [Fact]
    public void ToolCallOutcome_IsOk_TheRefusalCode_OrError()
    {
        static ModelContextProtocol.Protocol.CallToolResult Result(bool isError, string text) => new()
        {
            IsError = isError,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }]
        };

        Projector.Mcp.Server.Tools.ToolCallLogFilter.Outcome(Result(false, "{}")).Should().Be("ok");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.Outcome(Result(true, "{\"error\":\"projector_permission_denied\",\"message\":\"x\"}"))
            .Should().Be("projector_permission_denied");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.Outcome(Result(true, "An error occurred invoking 'x'.")).Should().Be("error");
    }

    [Fact]
    public void ToolCallHeaderNames_AreSortedNamesWithoutValues()
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary
        {
            ["User-Agent"] = "Sydney",
            ["Authorization"] = "Bearer secret-token",
            ["Accept"] = "application/json"
        };

        Projector.Mcp.Server.Tools.ToolCallLogFilter.HeaderNames(headers).Should().Be("Accept,Authorization,User-Agent");
    }

    [Fact]
    public void ToolCallClient_FallsBackToUserAgent_AndSessionToConversationId()
    {
        Projector.Mcp.Server.Tools.ToolCallLogFilter.ClientName("copilot-cowork", "0.1.0", "ua").Should().Be("copilot-cowork 0.1.0");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.ClientName(null, null, "Some-Agent/1.0").Should().Be("ua:Some-Agent/1.0");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.ClientName(null, null, null).Should().Be("unknown");

        var copilot = new Microsoft.AspNetCore.Http.HeaderDictionary { ["X-Microsoft-AI-ConversationId"] = "conv-1" };
        Projector.Mcp.Server.Tools.ToolCallLogFilter.SessionOrConversationId(copilot).Should().Be("conv-1");
        copilot["Mcp-Session-Id"] = "mcp-1";
        Projector.Mcp.Server.Tools.ToolCallLogFilter.SessionOrConversationId(copilot).Should().Be("mcp-1");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.SessionOrConversationId(null).Should().BeNull();
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static SaveTimecardInput Input(
        string? uid = null,
        string date = "2026-09-24",
        double hours = 1.5,
        string task = "development",
        string role = "System Engineer",
        string narrative = "Fix build pipeline") =>
        new(date, hours, "P005678-001", task, role, narrative, uid);

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

    /// <summary>
    /// The task tree fixture with its parent tasks (User Story 101 / 202) open for time, which makes them summary tasks
    /// Projector still reports as open; optionally with AllowAssignmentFlag=false (time only on assigned tasks).
    /// </summary>
    private static TimeEntryProjectSetup TreeWithOpenParents(bool allowAssignment = true)
    {
        var xml = File.ReadAllText(Path.Combine(FixturesDir, "time_entry_project_tree.xml"))
            .Replace("<a:OpenForTimeFlag>false</a:OpenForTimeFlag>", "<a:OpenForTimeFlag>true</a:OpenForTimeFlag>");
        if (!allowAssignment)
        {
            xml = xml.Replace("<a:AllowAssignmentFlag>true</a:AllowAssignmentFlag>", "<a:AllowAssignmentFlag>false</a:AllowAssignmentFlag>");
        }

        return ProjectorTimeEntryParsers.ParseTimeEntryProject(XDocument.Parse(xml))!;
    }

    private static (TimeEntryToolService Service, FakeTimeEntryClient Fake) CreateService(
        bool tree = false,
        Microsoft.Extensions.Logging.ILogger<TimeEntryToolService>? logger = null,
        IProjectorScheduleClient? schedule = null)
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
        return (new TimeEntryToolService(
            connections, fake, new TimeEntryCache(), logger ?? NullLogger<TimeEntryToolService>.Instance, schedule), fake);
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

        /// <summary>When set, <see cref="SaveException"/> is thrown only on that save call (1-based).</summary>
        public int? SaveExceptionOnCall { get; set; }
        public List<Timecard> DayCards { get; set; } = [];
        public Exception? DayCardsException { get; set; }
        public List<TimecardSaveRequest> Saves { get; } = [];
        public int SaveCalls { get; private set; }

        /// <summary>Projector read calls per kind, to check what the cache saves.</summary>
        public Dictionary<string, int> Calls { get; } = new() { ["projects"] = 0, ["setup"] = 0, ["rules"] = 0, ["day_cards"] = 0, ["assignments"] = 0, ["recent"] = 0 };

        /// <summary>The user's cards of the recent window (list_time_projects' recent usage).</summary>
        public List<Timecard> RecentCards { get; set; } = [];

        public Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
            ProjectorConnection connection, string startDate, string endDate, CancellationToken cancellationToken = default)
        {
            Calls["recent"]++;
            return Task.FromResult<IReadOnlyList<Timecard>>(RecentCards.ToList());
        }

        public TaskAssignments Assignments { get; set; } = new(false, new Dictionary<string, IReadOnlySet<string>>());
        public Exception? AssignmentsException { get; set; }

        public Task<TaskAssignments> GetTaskAssignmentsAsync(
            ProjectorConnection connection, string projectCode, CancellationToken cancellationToken = default)
        {
            Calls["assignments"]++;
            return AssignmentsException is not null
                ? Task.FromException<TaskAssignments>(AssignmentsException)
                : Task.FromResult(Assignments);
        }

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
            if (SaveException is not null && (SaveExceptionOnCall is null || SaveExceptionOnCall == SaveCalls))
            {
                throw SaveException;
            }

            Saves.Add(request);
            return Task.FromResult(new TimecardSaveResult
            {
                TimecardUid = SaveCalls == 1 ? SaveResult.TimecardUid : $"{SaveResult.TimecardUid}{SaveCalls}",
                WorkDate = SaveResult.WorkDate,
                WorkMinutes = SaveResult.WorkMinutes,
                CardStatusCode = SaveResult.CardStatusCode,
                SubmittedFlag = SaveResult.SubmittedFlag
            });
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

    // ---- size and rows in the logs: per tool call and per Projector call -------------------

    [Fact]
    public void ProjectorRows_AreTheMethodsRecordElements_WithOrWithoutPrefix()
    {
        const string xml =
            "<s:Envelope><a:PwsResourceSummary><a:Name>x</a:Name></a:PwsResourceSummary>" +
            "<PwsResourceSummary></PwsResourceSummary><a:PwsResourceSummaryExtra></a:PwsResourceSummaryExtra>" +
            "<a:PwsResourceSummary i:nil=\"true\"/></s:Envelope>";

        ProjectorCallStats.CountRows("PwsGetResourceList", xml).Should().Be(2, "only closed PwsResourceSummary elements are rows");
        ProjectorCallStats.CountRows("PwsGetTimeCards", "<a:PwsTimecardDetail></a:PwsTimecardDetail><a:PwsTimeOffCardDetail></a:PwsTimeOffCardDetail>")
            .Should().Be(2, "work and time-off cards come from the same method");
        ProjectorCallStats.CountRows("PwsSaveTimeCards", xml).Should().BeNull("a method without a record element has no row count");
        ProjectorCallStats.CountRows("PwsGetResourceList", "").Should().BeNull();
    }

    [Fact]
    public async Task ProjectorCall_IsMeasured_AndTheCallerStillReadsTheResponse()
    {
        const string body =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><R>" +
            "<PwsResourceSummary><Name>a</Name></PwsResourceSummary><PwsResourceSummary><Name>b</Name></PwsResourceSummary>" +
            "</R></s:Body></s:Envelope>";
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var services = new ServiceCollection().AddLogging();
        services.AddProjectorApiClient();
        services.AddHttpClient<ProjectorSoapHttp>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var soap = provider.GetRequiredService<ProjectorSoapHttp>();

        var collector = ProjectorCallStats.Begin();
        var doc = await soap.PostWcfAsync(Connection(), "PwsGetResourceList", new XElement("x"));

        doc.Descendants("PwsResourceSummary").Should().HaveCount(2);
        var stat = collector.Calls.Should().ContainSingle().Subject;
        stat.Action.Should().Be("PwsGetResourceList");
        stat.Status.Should().Be("200");
        stat.Rows.Should().Be(2);
        stat.ResponseBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task FailedProjectorCall_IsMeasuredToo_WithoutSizeOrRows()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddProjectorApiClient();
        services.AddHttpClient<ProjectorSoapHttp>()
            .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(new HttpRequestException("connection reset")));
        using var provider = services.BuildServiceProvider();
        var soap = provider.GetRequiredService<ProjectorSoapHttp>();

        var collector = ProjectorCallStats.Begin();
        var act = () => soap.PostWcfAsync(Connection(), "PwsGetEngagementList", new XElement("x"));

        await act.Should().ThrowAsync<HttpRequestException>();
        var stat = collector.Calls.Should().ContainSingle().Subject;
        stat.Status.Should().Be("exception:HttpRequestException");
        stat.ResponseBytes.Should().BeNull();
        stat.Rows.Should().BeNull();
    }

    [Fact]
    public void ToolCallArguments_LogTheShape_NeverNamesCodesOrSearchText()
    {
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        var args = new Dictionary<string, JsonElement>
        {
            ["start_date"] = J("\"2026-09-01\""),
            ["end_date"] = J("\"2026-09-30\""),
            ["resource_id"] = J("\"Jane Doe\""),
            ["query"] = J("\"invoice export\""),
            ["compact"] = J("true"),
            ["max_rows"] = J("50"),
            ["project_codes"] = J("[\"P001234-001\",\"P005678-001\"]"),
            ["status"] = J("null")
        };

        var info = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(args);

        info.Names.Should().Be("compact,end_date,max_rows,project_codes,query,resource_id,start_date");
        info.Values.Should().Be("compact=true;max_rows=50;project_codes=[2]");
        info.DateSpanDays.Should().Be(30);
        info.StartDate.Should().Be("2026-09-01");
        info.EndDate.Should().Be("2026-09-30");
        info.WorkDate.Should().BeNull();
        Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(
                new Dictionary<string, JsonElement> { ["work_date"] = J("\"2026-10-02\""), ["project_code"] = J("\"P001234-001\"") })
            .WorkDate.Should().Be("2026-10-02");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(null).Names.Should().BeEmpty();
    }

    [Fact]
    public void ToolCallArguments_HashMatchesForTheSameCallOnly_AndHidesTheValues()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Dictionary<string, JsonElement> Args(string person) => new()
        {
            ["resource"] = J($"\"{person}\""),
            ["start_date"] = J("\"2026-11-01\""),
            ["dry_run"] = J("false")
        };

        var a = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(Args("Jane Doe"));
        var again = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(Args("Jane Doe"));
        var other = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(Args("John Roe"));

        a.Hash.Should().MatchRegex("^[0-9a-f]{8}$");
        again.Hash.Should().Be(a.Hash);
        other.Hash.Should().NotBe(a.Hash);
        a.Values.Should().Be("dry_run=false").And.NotContain("Jane");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeArguments(null).Hash.Should().BeNull();
    }

    [Fact]
    public void ToolCallLine_WriteToolWithoutDryRun_SaysTheDefaultWasUsed()
    {
        var logger = new ListLogger();
        var call = new Projector.Mcp.Server.Tools.ToolCallLogFilter.CallInfo(
            "save_booking", null, "Sydney 1.0.0", "0.0.0", null, null, null, null);

        Projector.Mcp.Server.Tools.ToolCallLogFilter.Log(
            logger, call, "ok", TimeSpan.FromMilliseconds(10), exception: null,
            new Projector.Mcp.Server.Tools.ToolCallLogFilter.ArgumentInfo("project_code,resource", "", null, Hash: "0a1b2c3d"),
            output: null, []);
        Projector.Mcp.Server.Tools.ToolCallLogFilter.Log(
            logger, call with { Tool = "list_timecards" }, "ok", TimeSpan.FromMilliseconds(10), exception: null,
            new Projector.Mcp.Server.Tools.ToolCallLogFilter.ArgumentInfo("end_date,start_date", "", 5),
            output: null, []);

        logger.Entries[0].Message.Should().Contain("args hash 0a1b2c3d, dry_run defaulted True");
        logger.Entries[1].Message.Should().EndWith("dry_run defaulted (null)", "only write tools have a dry_run default");
    }

    [Fact]
    public void ToolCallOutput_HasSizeRowsAndPagingFlags()
    {
        static ModelContextProtocol.Protocol.CallToolResult Result(string text, bool isError = false) => new()
        {
            IsError = isError,
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }]
        };

        const string cut = "{\"resources\":[1,2,3],\"count\":3,\"has_more\":false,\"searchCoverage\":{\"status\":\"partial\"}}";
        var list = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeOutput(Result(cut));
        list.Bytes.Should().Be(cut.Length);
        list.Rows.Should().Be(3);
        list.HasMore.Should().BeFalse();
        list.Partial.Should().BeTrue();

        var tasks = Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeOutput(
            Result("{\"tasks\":[1,2],\"tasks_count\":2,\"tasks_total\":134,\"tasks_has_more\":true}"));
        tasks.Rows.Should().Be(2);
        tasks.Total.Should().Be(134);
        tasks.HasMore.Should().BeTrue();
        tasks.Partial.Should().BeNull();

        Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeOutput(Result("{\"days\":[1,2,3,4],\"weeks\":[1]}"))
            .Rows.Should().Be(4, "without a count the longest list is the row count");
        Projector.Mcp.Server.Tools.ToolCallLogFilter.DescribeOutput(Result("{\"error\":\"x\",\"message\":\"y\"}", isError: true))
            .Rows.Should().BeNull();
    }

    [Fact]
    public void ToolCallLine_CarriesOutputAndProjectorTotals()
    {
        var logger = new ListLogger();
        var call = new Projector.Mcp.Server.Tools.ToolCallLogFilter.CallInfo(
            "list_timecards", null, "Sydney 1.0.0", "0.0.0", null, null, null, null);

        Projector.Mcp.Server.Tools.ToolCallLogFilter.Log(
            logger, call, "ok", TimeSpan.FromMilliseconds(1500), exception: null,
            new Projector.Mcp.Server.Tools.ToolCallLogFilter.ArgumentInfo("end_date,start_date", "", 5, "2026-09-21", "2026-09-25"),
            new Projector.Mcp.Server.Tools.ToolCallLogFilter.OutputInfo(27 * 1024, 61, null, null, false),
            [
                new ProjectorCallStat("PwsGetTimeCards", 1200, 512 * 1024, 61, "200"),
                new ProjectorCallStat("PwsGetResource", 100, 2048, null, "200")
            ]);

        logger.Entries.Should().ContainSingle().Which.Message.Should()
            .Contain("list_timecards ok in 1500 ms, 27 KB, 61 rows")
            .And.Contain("Projector 2 call(s), 1300 ms, 514 KB, 61 rows")
            .And.Contain("partial False")
            .And.Contain("args [end_date,start_date] , dates 2026-09-21..2026-09-25 (5 day(s))");
    }
}
