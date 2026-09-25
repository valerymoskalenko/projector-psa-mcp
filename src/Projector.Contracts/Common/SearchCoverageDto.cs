using System.Text.Json.Serialization;
using Projector.Domain.Pagination;

namespace Projector.Contracts.Common;

public sealed record SearchCoverageDto(
    bool Complete,
    bool CountIsLowerBound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SearchedScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Suggestion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LocationProbes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HolidayCalls = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Returned = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Limit = null)
{
    /// <summary>Agent-facing completeness: "full" or "partial".</summary>
    public string Status => Complete ? SearchCoverage.StatusFull : SearchCoverage.StatusPartial;

    public static SearchCoverageDto From(SearchCoverage coverage) => new(
        coverage.Complete,
        coverage.CountIsLowerBound,
        coverage.SearchedScope,
        coverage.Reason,
        coverage.Suggestion,
        coverage.LocationProbes,
        coverage.HolidayCalls,
        coverage.Returned,
        coverage.Limit);
}
