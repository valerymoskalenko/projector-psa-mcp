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
    public void TimeEntryPrompts_SayAnswersAreNotApproval()
    {
        var rule = Projector.Mcp.Server.Prompts.ProjectorPrompts.SaveConfirmationRule;
        rule.Should().Contain("not approval").And.Contain("Save these N cards?");
        Projector.Mcp.Server.Prompts.ProjectorPrompts.ReviewMyDaySteps.Should().Contain(rule);
        Projector.Mcp.Server.Prompts.ProjectorPrompts.LogTime("2026-09-24", 1, "P005678-001", "Work").Text
            .Should().Contain(rule);
        Projector.Mcp.Server.Prompts.ProjectorPrompts.MyTimecards("2026-09-01", "2026-09-30").Text
            .Should().Contain("without resource_id").And.NotContain("get_resource email");
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
