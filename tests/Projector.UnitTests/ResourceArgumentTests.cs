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
                default:
                    throw new NotSupportedException($"Unexpected Projector call {targetMethod.Name}");
            }
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
