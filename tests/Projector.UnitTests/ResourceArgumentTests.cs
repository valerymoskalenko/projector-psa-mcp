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

        public static RecordingSoap Create() => (RecordingSoap)(object)Create<IProjectorSoapClient, RecordingSoap>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IProjectorSoapClient.ListTimecardsAsync):
                    Calls.Add($"ListTimecardsAsync({(args![1] as string) ?? "<none>"})");
                    return Task.FromResult(new TimecardListResult { Timecards = [] });
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
