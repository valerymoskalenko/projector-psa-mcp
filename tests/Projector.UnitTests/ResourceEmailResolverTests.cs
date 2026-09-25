using FluentAssertions;
using Projector.Application.Resources;
using Projector.Domain.Auth;
using Projector.Domain.Resources;

namespace Projector.UnitTests;

/// <summary>
/// Email lookup against a fake that behaves like Projector: search matches display names, not emails.
/// </summary>
public class ResourceEmailResolverTests
{
    private static readonly ResourceSummary[] People =
    [
        new() { ResourceReferenceSystemId = "10001", DisplayName = "Jane Doe", EmailAddress = "jane.doe@contoso.com" },
        new() { ResourceReferenceSystemId = "10002", DisplayName = "Jane Smith", EmailAddress = "jane.smith@contoso.com" },
        new() { ResourceReferenceSystemId = "10003", DisplayName = "Robert Brown", EmailAddress = "bob@contoso.com" }
    ];

    private static readonly ProjectorConnection Connection = new()
    {
        ConnectionId = "test",
        SessionTicket = "ticket",
        RefreshToken = "refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        GrantedScope = "allowFullPermissions",
        SoapServiceAuthority = "https://example.invalid",
        RestServiceAuthority = "https://example.invalid"
    };

    [Fact]
    public async Task FindsExactEmailViaNameSearch()
    {
        var client = new NameOnlySearchClient(People);
        var match = await ResourceEmailResolver.FindAsync(client, Connection, " Jane.Smith@Contoso.com ");
        match!.ResourceReferenceSystemId.Should().Be("10002");
        client.Queries.Should().NotContain((string?)null, "a name token finds the person without a full scan");
    }

    [Fact]
    public async Task FallsBackToWideListWhenNameTokensDoNotMatch()
    {
        var client = new NameOnlySearchClient(People);
        var match = await ResourceEmailResolver.FindAsync(client, Connection, "bob@contoso.com");
        match!.ResourceReferenceSystemId.Should().Be("10003");
        client.Queries.Should().EndWith((string?)null);
    }

    [Fact]
    public async Task ReturnsNullInsteadOfAnotherPerson()
    {
        var client = new NameOnlySearchClient(People);
        var match = await ResourceEmailResolver.FindAsync(client, Connection, "jane.nobody@contoso.com");
        match.Should().BeNull("a name search hit for 'jane' must not be returned for a different email");
    }

    [Theory]
    [InlineData("jane.doe@contoso.com", true)]
    [InlineData("10001", false)]
    [InlineData("Jane Doe", false)]
    [InlineData(null, false)]
    public void LooksLikeEmail(string? value, bool expected) =>
        ResourceEmailResolver.LooksLikeEmail(value).Should().Be(expected);

    private sealed class NameOnlySearchClient(IReadOnlyList<ResourceSummary> people) : IProjectorResourceClient
    {
        public List<string?> Queries { get; } = [];

        public Task<ResourceListResult> ListResourcesAsync(
            ProjectorConnection connection, string? query, bool includeInactive, int maxRows,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var hits = people
                .Where(p => query is null || p.DisplayName!.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(maxRows)
                .ToList();
            return Task.FromResult(new ResourceListResult { Resources = hits });
        }

        public Task<ResourceDetail?> GetResourceAsync(
            ProjectorConnection connection, string id, bool includeHistory, bool includeUdfs,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
