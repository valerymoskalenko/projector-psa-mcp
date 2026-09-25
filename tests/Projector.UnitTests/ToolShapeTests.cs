using System.Text.Json;
using FluentAssertions;
using Projector.Contracts.Common;
using Projector.Contracts.Holidays;
using Projector.Contracts.Resources;
using Projector.Domain.Pagination;
using Projector.Mcp.Server.Tools;

namespace Projector.UnitTests;

/// <summary>
/// Asserts agent-visible JSON keys without inventing Projector payloads.
/// Uses DTOs / AttachDuration-shaped dictionaries built from empty real structures.
/// </summary>
public class ToolShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void ListHolidaysResponse_SerializesSnakeCase()
    {
        var dto = new ListHolidaysResponse(
            "2026-09-01",
            "2026-12-31",
            Location: null,
            Calendars: [],
            Count: 0,
            ActiveResourceCount: 0,
            SearchCoverage: SearchCoverageDto.From(SearchCoverage.Full("location holiday calendars")));

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("start_date", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("end_date", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("active_resource_count", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("location", out _).Should().BeFalse();
        doc.RootElement.GetProperty("searchCoverage").GetProperty("status").GetString().Should().Be("full");
    }

    [Fact]
    public void SearchCoverage_FullAndPartial_MapStatus()
    {
        var full = SearchCoverage.Full("open engagements only", returned: 3, limit: 50);
        full.Complete.Should().BeTrue();
        full.Status.Should().Be("full");
        full.CountIsLowerBound.Should().BeFalse();
        full.Reason.Should().BeNull();

        var partial = SearchCoverage.Partial(
            "open engagements only",
            "Projector hit its 50-engagement row cap.",
            "Narrow the query.",
            returned: 50,
            limit: 50);
        partial.Complete.Should().BeFalse();
        partial.Status.Should().Be("partial");
        partial.CountIsLowerBound.Should().BeTrue();
        partial.Suggestion.Should().Be("Narrow the query.");

        var dto = SearchCoverageDto.From(partial);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("partial");
        doc.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("countIsLowerBound").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("searchedScope").GetString().Should().Be("open engagements only");
    }

    [Fact]
    public void PageResultFactory_IncludesStatusAndReturned()
    {
        var page = PageResultFactory.Create(
            [1, 2, 3],
            maxRows: 2,
            serverTruncated: true,
            searchedScope: "open engagements only");
        page.SearchCoverage.Status.Should().Be("partial");
        page.SearchCoverage.Returned.Should().Be(2);
        page.SearchCoverage.Limit.Should().Be(2);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public void GetResourceResponse_HasUriAndLinks()
    {
        var dto = new GetResourceResponse(
            "projector://resources/10001",
            new ResourceDetailDto(
                null, "10001", "Jane Doe", null, null, null, false,
                null, null, null, null, null, null, null, null, null, null, null),
            [
                new ResourceLinkDto("projector://resources/10001", "Jane Doe"),
                new ResourceLinkDto("projector://resources/10001/history", "history"),
                new ResourceLinkDto("projector://resources/10001/udfs", "udfs")
            ]);

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("uri").GetString().Should().StartWith("projector://resources/");
        doc.RootElement.GetProperty("resource_links").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void EngagementDetailDto_OmitsNullMoneyBudgets()
    {
        var dto = new Projector.Contracts.Engagements.EngagementDetailDto(
            "E100",
            "Sample",
            true,
            true,
            "Contoso - US",
            "Contoso",
            "C000001",
            "Betty Smith",
            "betty@example.com",
            [],
            [],
            BudgetVisibility: "hours_only",
            WorkMinutesTimeBudgetAmount: 4800,
            WorkHoursTimeBudgetAmount: 80);

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("budgetVisibility").GetString().Should().Be("hours_only");
        doc.RootElement.GetProperty("workMinutesTimeBudgetAmount").GetInt32().Should().Be(4800);
        doc.RootElement.GetProperty("workHoursTimeBudgetAmount").GetDouble().Should().Be(80);
        doc.RootElement.TryGetProperty("contractRevenueTimeBudgetAmount", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("currencyCode", out _).Should().BeFalse();
    }

    [Fact]
    public void ToolOutputSchemas_ExportHolidaysJsonSchema()
    {
        var schema = ToolOutputSchemas.HolidaysJsonSchema;
        schema.Should().Contain("start_date");
        schema.Should().Contain("active_resource_count");
    }

    [Fact]
    public void ToolOutputSchemas_DocumentSearchCoverageStatus()
    {
        ToolOutputSchemas.SearchCoverageRule.Should().Contain("partial");
        ToolOutputSchemas.ProjectRolesSchemaHint.Should().Contain("searchCoverage");
        ToolOutputSchemas.ProjectBookingsSchemaHint.Should().Contain("searchCoverage");
        ToolOutputSchemas.TimecardsSchemaHint.Should().Contain("searchCoverage");
    }
}
