using System.Text.Json;
using FluentAssertions;
using System.Reflection;
using ModelContextProtocol.Server;
using Projector.Mcp.Server.Cli;
using Projector.Mcp.Server.Tools;

namespace Projector.UnitTests;

/// <summary>
/// Catalog parity against tools.json (names, aliases, whenNotToUse, order).
/// </summary>
public class CatalogTests
{
    private static readonly string ToolsJsonPath = ResolveToolsJson();

    private static string ResolveToolsJson()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Projector.Mcp.Server", "tools.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "src", "Projector.Mcp.Server", "tools.json"))
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("tools.json not found.");
    }

    [Fact]
    public void CanonicalAgentTools_MatchToolsJsonOrderAndNames()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ToolsJsonPath));
        var agentTools = doc.RootElement.GetProperty("agentTools");
        var names = agentTools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();

        names.Should().Equal(ToolCatalog.CanonicalAgentTools);
    }

    [Fact]
    public void Aliases_ResolveToCanonical()
    {
        ToolCatalog.Canonicalize("projector_list_users").Should().Be("list_resources");
        ToolCatalog.Canonicalize("projector_get_user").Should().Be("get_resource");
        ToolCatalog.Canonicalize("projector_get_timecards").Should().Be("list_timecards");
        ToolCatalog.Canonicalize("projector_get_time_off").Should().Be("list_time_off");
        ToolCatalog.Canonicalize("projector_upcoming_pto").Should().Be("list_upcoming_pto");
        ToolCatalog.Canonicalize("projector_check_booking").Should().Be("check_availability");
        ToolCatalog.Canonicalize("projector_get_resource_schedule").Should().Be("get_schedule");
        ToolCatalog.Canonicalize("list_project_bookings").Should().Be("list_proj_bookings");
    }

    [Fact]
    public void LegacyPrefixedNames_ResolveToCanonical()
    {
        foreach (var name in ToolCatalog.CanonicalAgentTools)
        {
            ToolCatalog.Canonicalize("projector_" + name).Should().Be(name);
            ToolCatalog.Canonicalize(name).Should().Be(name);
        }
    }

    [Theory]
    [InlineData("resource", "get_resource")]
    [InlineData("resources", "list_resources")]
    [InlineData("schedule", "get_schedule")]
    [InlineData("availability", "check_availability")]
    [InlineData("time_off", "list_time_off")]
    [InlineData("engagement", "get_engagement")]
    [InlineData("engagements", "list_engagements")]
    [InlineData("upcoming_pto", "list_upcoming_pto")]
    [InlineData("proj_bookings", "list_proj_bookings")]
    [InlineData("projector_get_resource", "get_resource")]
    [InlineData("list_project_bookings", "list_proj_bookings")]
    public void CopilotStrippedNames_ResolveToRegisteredTool(string called, string expected)
    {
        CopilotToolNameFilter.Resolve(called, ToolCatalog.CanonicalAgentTools).Should().Be(expected);
    }

    [Fact]
    public void CopilotStrippedNames_EveryToolIsReachableAfterStripping()
    {
        // Copilot calls everything after the first underscore; each result must map back to exactly one tool.
        foreach (var name in ToolCatalog.CanonicalAgentTools)
        {
            var stripped = name[(name.IndexOf('_') + 1)..];
            CopilotToolNameFilter.Resolve(stripped, ToolCatalog.CanonicalAgentTools).Should().Be(name);
        }
    }

    [Theory]
    [InlineData("get_resource")]
    [InlineData("unknown_thing")]
    [InlineData("")]
    public void CopilotStrippedNames_LeaveExactOrUnknownNamesAlone(string called)
    {
        CopilotToolNameFilter.Resolve(called, ToolCatalog.CanonicalAgentTools).Should().BeNull();
    }

    [Fact]
    public void McpTools_AreRegisteredOnceAndMatchCatalog()
    {
        var registered = typeof(AgentTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToList();

        registered.Should().OnlyHaveUniqueItems();
        registered.Should().BeEquivalentTo(ToolCatalog.CanonicalAgentTools);
        foreach (var name in registered)
        {
            // Agent 365 / MOS BYO MCP registration rejects names longer than 30 characters.
            name!.Length.Should().BeLessThanOrEqualTo(30, because: name);
            name.Should().MatchRegex("^[a-z][a-z0-9_]*$");
        }
    }

    [Fact]
    public void ToolsJson_HasWhenNotToUse_ForEachAgentTool()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ToolsJsonPath));
        foreach (var tool in doc.RootElement.GetProperty("agentTools").EnumerateArray())
        {
            tool.TryGetProperty("whenNotToUse", out var wnt).Should().BeTrue(
                because: $"{tool.GetProperty("name").GetString()} should declare whenNotToUse");
            wnt.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void CheckAvailability_AcceptsResourceIdAliasInSchema()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ToolsJsonPath));
        var check = doc.RootElement.GetProperty("agentTools").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "check_availability");
        check.GetProperty("inputSchema").GetProperty("properties")
            .TryGetProperty("resource_id", out _).Should().BeTrue();
        check.GetProperty("inputSchema").GetProperty("properties")
            .TryGetProperty("show_availability_days", out var showDays).Should().BeTrue();
        showDays.GetProperty("default").GetBoolean().Should().BeFalse();
    }
}
