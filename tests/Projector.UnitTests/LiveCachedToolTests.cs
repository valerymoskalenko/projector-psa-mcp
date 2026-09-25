using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Application;
using Projector.Application.Auth;
using Projector.Application.Resources;
using Projector.Application.Tools;
using Projector.Contracts.Resources;
using Projector.Domain.Auth;
using Projector.Mcp.Server.Cli;

namespace Projector.UnitTests;

/// <summary>
/// Live read-only tools using the DPAPI OAuth cache.
/// Fails with a login instruction when no usable live cache exists.
/// Compares shapes/counts — not pinned live hour totals.
/// Excluded by default: run with <c>--filter Category=Live</c>.
/// Tenant-specific inputs come from environment variables (see <see cref="LiveSettings"/>).
/// </summary>
[Trait("Category", "Live")]
public class LiveCachedToolTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string LoginHelp =
        "No usable live Projector OAuth cache. From the repo root run:\n" +
        "  $env:ASPNETCORE_ENVIRONMENT = \"Development\"\n" +
        "  dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- auth login\n" +
        "Then re-run tests. Cache path: %LOCALAPPDATA%\\ProjectorMcp\\oauth-sessions";

    private ServiceProvider? _sp;
    private string? _connectionId;
    private string? _skipReason;

    public Task InitializeAsync()
    {
        if (LiveSettings.MissingRequired() is { } missing)
        {
            _skipReason = missing;
            return Task.CompletedTask;
        }

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<ProjectorOptions>(o =>
        {
            o.AccountCode = LiveSettings.AccountCode!;
            o.RequestedScopes = ProjectorScopes.AllowFullPermissions;
            o.PublicBaseUrl = "http://localhost:5180";
            o.RedirectUri = "http://localhost:5180/oauth/projector/callback";
            o.KeyVaultUri = LiveSettings.KeyVaultUri;
            o.AuthorizeBaseUrl = "https://app.projectorpsa.com/oauth2authorize";
            o.TokenUrl = "https://app.projectorpsa.com/oauth2token";
            o.RevokeUrl = "https://app.projectorpsa.com/oauth2revoketoken";
        });
        services.AddProjectorApplication();
        services.AddProjectorApiClient();
        _sp = services.BuildServiceProvider();

        try
        {
            var login = _sp.GetRequiredService<LocalOAuthLoginService>();
            var connection = login.LoginAsync(forceLogin: false, openBrowser: false, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!connection.SoapServiceAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || connection.SoapServiceAuthority.Contains("mock", StringComparison.OrdinalIgnoreCase))
            {
                _skipReason = LoginHelp + " (cache SOAP authority is not a live Projector host).";
                return Task.CompletedTask;
            }

            _connectionId = connection.ConnectionId;
        }
        catch (Exception ex)
        {
            _skipReason = LoginHelp + "\nDetails: " + ex.Message;
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_sp is not null)
        {
            await _sp.DisposeAsync();
        }
    }

    private void RequireLive()
    {
        if (_skipReason is not null || _connectionId is null || _sp is null)
        {
            Assert.Fail(_skipReason ?? LoginHelp);
        }
    }

    private static JsonElement AsElement(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task GetResource_ById_ReturnsUriAndLinks()
    {
        RequireLive();

        var resources = _sp!.GetRequiredService<ResourceService>();
        foreach (var (resourceId, email) in LiveSettings.Resources)
        {
            var result = await resources.GetAsync(
                _connectionId!,
                new GetResourceRequest(resourceId, false, true),
                CancellationToken.None);
            result.Uri.Should().Be($"projector://resources/{resourceId}");
            result.ResourceLinks.Should().Contain(l => l.Uri.EndsWith("/history"));
            result.Resource.ResourceReferenceSystemId.Should().Be(resourceId);
            if (!string.IsNullOrWhiteSpace(email))
            {
                result.Resource.EmailAddress.Should().BeEquivalentTo(email);
            }

            result.Resource.DisplayName.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task GetResource_ByEmail_ResolvesToSameResource()
    {
        RequireLive();

        var resources = _sp!.GetRequiredService<ResourceService>();
        foreach (var (resourceId, email) in LiveSettings.Resources.Where(r => r.Email is not null))
        {
            var result = await resources.GetAsync(
                _connectionId!,
                new GetResourceRequest(email!, false, false),
                CancellationToken.None);
            result.Resource.ResourceReferenceSystemId.Should().Be(resourceId);
            result.Resource.EmailAddress.Should().BeEquivalentTo(email);
        }
    }

    [Fact]
    public async Task ListResources_HasSearchCoverageAndLinks()
    {
        RequireLive();
        var resources = _sp!.GetRequiredService<ResourceService>();
        var result = await resources.ListAsync(
            _connectionId!,
            new ListResourcesRequest(LiveSettings.ResourceSearch, false, 10),
            CancellationToken.None);
        result.SearchCoverage.Should().NotBeNull();
        result.ResourceLinks.Should().NotBeEmpty();
        result.Count.Should().Be(result.Resources.Count);
    }

    [Fact]
    public async Task ListUpcomingPto_SourcesAndCount()
    {
        RequireLive();

        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.ListUpcomingPtoAsync(
            _connectionId!, LiveSettings.PrimaryResourceId, "2026-09-01", "2026-12-30", CancellationToken.None);
        var root = AsElement(result);
        root.GetProperty("resource_id").GetString().Should().Be(LiveSettings.PrimaryResourceId);
        if (LiveSettings.ExpectedPtoCount is { } expected)
        {
            root.GetProperty("count").GetInt32().Should().Be(expected);
        }
        var allowed = new HashSet<string> { "holiday", "schedule_timeoff", "timecard" };
        foreach (var item in root.GetProperty("pto").EnumerateArray())
        {
            allowed.Should().Contain(item.GetProperty("source").GetString());
            item.TryGetProperty("date", out _).Should().BeTrue();
            item.TryGetProperty("reason", out _).Should().BeTrue();
            item.TryGetProperty("minutes", out var minutes).Should().BeTrue();
            item.TryGetProperty("hours", out var hours).Should().BeTrue();
            hours.GetDouble().Should().Be(minutes.GetDouble() / 60.0);
        }
    }

    [Fact]
    public async Task ListHolidays_UsesSnakeCaseKeys()
    {
        RequireLive();
        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.ListHolidaysAsync(
            _connectionId!, "2026-09-01", "2026-12-31", location: null, CancellationToken.None);
        var root = AsElement(result);
        root.TryGetProperty("start_date", out _).Should().BeTrue();
        root.TryGetProperty("end_date", out _).Should().BeTrue();
        root.TryGetProperty("active_resource_count", out _).Should().BeTrue();
        root.TryGetProperty("startDate", out _).Should().BeFalse();
        root.TryGetProperty("location", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ListEngagements_HasManagersOrProjectsAfterEnrichment()
    {
        RequireLive();
        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.ListEngagementsAsync(
            _connectionId!, query: "E", managerQuery: null, managerRole: null,
            includeClosed: true, maxRows: 5, CancellationToken.None);
        var root = AsElement(result);
        root.TryGetProperty("searchCoverage", out var coverage).Should().BeTrue();
        root.TryGetProperty("resource_links", out _).Should().BeTrue();
        coverage.GetProperty("status").GetString().Should().BeOneOf("full", "partial");
        var engagements = root.GetProperty("engagements");
        engagements.GetArrayLength().Should().BeGreaterThan(0);
        var anyEnriched = engagements.EnumerateArray().Any(e =>
            (e.TryGetProperty("engagementManagerDisplayName", out var m) && m.ValueKind != JsonValueKind.Null)
            || (e.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0));
        anyEnriched.Should().BeTrue("list_engagements should enrich managers/projects");
    }

    [Fact]
    public async Task ListProjectRoles_HasSearchCoverageStatus()
    {
        RequireLive();
        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.ListProjectRolesAsync(
            _connectionId!, [LiveSettings.ProjectCode], CancellationToken.None);
        var root = AsElement(result);
        root.TryGetProperty("searchCoverage", out var coverage).Should().BeTrue();
        coverage.GetProperty("status").GetString().Should().Be("full");
        coverage.GetProperty("complete").GetBoolean().Should().BeTrue();
        root.GetProperty("count").GetInt32().Should().Be(root.GetProperty("roles").GetArrayLength());
    }

    [Fact]
    public async Task ListProjectBookings_HasSearchCoverageStatus()
    {
        RequireLive();
        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.ListProjectBookingsAsync(
            _connectionId!, [LiveSettings.ProjectCode], "2026-10-01", "2026-10-31", CancellationToken.None);
        var root = AsElement(result);
        root.TryGetProperty("searchCoverage", out var coverage).Should().BeTrue();
        coverage.GetProperty("status").GetString().Should().BeOneOf("full", "partial");
        root.TryGetProperty("bookings", out _).Should().BeTrue();
        root.TryGetProperty("failed_project_codes", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CheckAvailability_ResourceIdAlias_AndNativeBookings()
    {
        RequireLive();
        var tools = _sp!.GetRequiredService<ProjectorToolService>();
        var result = await tools.CheckAvailabilityAsync(
            _connectionId!,
            [LiveSettings.PrimaryResourceId],
            "2026-09-14",
            "2026-09-27",
            requiredHoursPerWeek: 30,
            requiredMinutesPerWeek: null,
            showAvailabilityDays: false,
            CancellationToken.None);
        var root = AsElement(result);
        root.TryGetProperty("start_date", out _).Should().BeTrue();
        root.TryGetProperty("required_minutes_per_week", out _).Should().BeTrue();
        root.TryGetProperty("searchCoverage", out var coverage).Should().BeTrue();
        coverage.GetProperty("status").GetString().Should().Be("full");
        root.GetProperty("people").GetArrayLength().Should().Be(1);
        var person = root.GetProperty("people")[0];
        person.TryGetProperty("user", out _).Should().BeTrue();
        person.TryGetProperty("resource", out _).Should().BeTrue();
        var availability = person.GetProperty("availability");
        availability.TryGetProperty("weeks", out _).Should().BeTrue();
        availability.TryGetProperty("days", out _).Should().BeFalse();
        var resource = person.GetProperty("resource");
        resource.TryGetProperty("history", out _).Should().BeFalse();
        if (availability.TryGetProperty("bookings", out var bookings) && bookings.GetArrayLength() > 0)
        {
            var first = bookings[0];
            // Native Projector rows expose scheduledMinutes / dailyWeeklyFlag, not exploded bookedMinutes-only.
            (first.TryGetProperty("scheduledMinutes", out _) || first.TryGetProperty("dailyWeeklyFlag", out _))
                .Should().BeTrue();
        }
    }

    [Fact]
    public async Task ListTimecards_September_ReturnsCards()
    {
        RequireLive();

        var result = await ToolCatalog.InvokeAsync(
            _sp!,
            "list_timecards",
            _connectionId!,
            new Dictionary<string, string>
            {
                ["resource_id"] = LiveSettings.PrimaryResourceId,
                ["start_date"] = "2026-09-01",
                ["end_date"] = "2026-09-30"
            },
            CancellationToken.None);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        json.Should().Contain("timecards");
        json.Should().Contain("searchCoverage");
    }
}

/// <summary>
/// Live test inputs for your Projector tenant, read from environment variables:
/// <list type="bullet">
/// <item><c>PROJECTOR_LIVE_ACCOUNT_CODE</c> (required): Projector account code.</item>
/// <item><c>PROJECTOR_LIVE_RESOURCES</c> (required): <c>id[:email];id[:email]...</c>. The first one is the primary resource (PTO, availability, timecards).</item>
/// <item><c>PROJECTOR_LIVE_PROJECT_CODE</c> (required): a project with roles and bookings, e.g. P001234-001.</item>
/// <item><c>PROJECTOR_LIVE_RESOURCE_SEARCH</c> (optional): name fragment for list_resources. Default "a".</item>
/// <item><c>PROJECTOR_LIVE_PTO_COUNT</c> (optional): expected PTO rows for the primary resource, 2026-09-01..2026-12-30.</item>
/// <item><c>PROJECTOR_LIVE_KEYVAULT_URI</c> (optional): Key Vault holding the dev OAuth client.</item>
/// </list>
/// </summary>
internal static class LiveSettings
{
    public static string? AccountCode => Env("PROJECTOR_LIVE_ACCOUNT_CODE");

    public static string? KeyVaultUri => Env("PROJECTOR_LIVE_KEYVAULT_URI");

    public static string ProjectCode => Env("PROJECTOR_LIVE_PROJECT_CODE") ?? "";

    public static string ResourceSearch => Env("PROJECTOR_LIVE_RESOURCE_SEARCH") ?? "a";

    public static int? ExpectedPtoCount =>
        int.TryParse(Env("PROJECTOR_LIVE_PTO_COUNT"), out var n) ? n : null;

    public static IReadOnlyList<(string Id, string? Email)> Resources =>
        (Env("PROJECTOR_LIVE_RESOURCES") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
                return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : (string?)null);
            })
            .ToList();

    public static string PrimaryResourceId => Resources.Count > 0 ? Resources[0].Id : "";

    public static string? MissingRequired()
    {
        var missing = new[] { "PROJECTOR_LIVE_ACCOUNT_CODE", "PROJECTOR_LIVE_RESOURCES", "PROJECTOR_LIVE_PROJECT_CODE" }
            .Where(name => Env(name) is null)
            .ToList();
        return missing.Count == 0
            ? null
            : "Live tests need environment variables: " + string.Join(", ", missing) +
              ". See LiveSettings in LiveCachedToolTests.cs.";
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
