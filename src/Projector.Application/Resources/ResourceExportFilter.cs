using Projector.Contracts.Resources;
using Projector.Domain.Holidays;

namespace Projector.Application.Resources;

/// <summary>
/// list_resources by cost center or department: filters the rows of Projector's resource export (which has these
/// fields; the resource list has not) and counts the groups so the agent can ask the user which one to use.
/// </summary>
public static class ResourceExportFilter
{
    public sealed record Result(
        IReadOnlyList<ExportedResourceRow> People,
        ResourceGroupsDto Groups,
        IReadOnlyList<string> MatchedCostCenters,
        IReadOnlyList<string> MatchedDepartments);

    /// <summary>
    /// Keeps the people whose cost center and department contain the given texts (case-insensitive; both must match
    /// when both are given) and whose name, e-mail or resource id contains <paramref name="query"/>. Inactive people
    /// count only when <paramref name="includeInactive"/>. Groups are counted over everyone visible, before the
    /// cost center / department filter, so they show every option.
    /// </summary>
    public static Result Apply(
        IReadOnlyList<ExportedResourceRow> rows,
        string? query,
        string? costCenter,
        string? department,
        bool includeInactive)
    {
        var visible = rows
            .Where(r => includeInactive || !r.Inactive)
            .Where(r => Matches(query, r.DisplayName, r.EmailAddress, r.ResourceReferenceSystemId))
            .ToList();

        var people = visible
            .Where(r => Matches(costCenter, r.CostCenterName))
            .Where(r => Matches(department, r.DepartmentName))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new ResourceGroupsDto(Count(visible, r => r.CostCenterName), Count(visible, r => r.DepartmentName));
        return new Result(
            people,
            groups,
            Distinct(people, r => r.CostCenterName, costCenter),
            Distinct(people, r => r.DepartmentName, department));
    }

    /// <summary>A short note for the agent: which groups matched, or that nothing did and which groups exist.</summary>
    public static string? Note(Result result, string? costCenter, string? department, bool truncated)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(costCenter) || !string.IsNullOrWhiteSpace(department))
        {
            if (result.People.Count == 0)
            {
                parts.Add("Nobody matched. groups lists the cost centers and departments that exist: ask the user "
                    + "which one they mean and call again with that name.");
            }
            else
            {
                if (result.MatchedCostCenters.Count > 1)
                {
                    parts.Add($"cost_center '{costCenter!.Trim()}' matched {result.MatchedCostCenters.Count} cost centers: "
                        + string.Join(", ", result.MatchedCostCenters) + ". Tell the user, or ask which one they mean.");
                }

                if (result.MatchedDepartments.Count > 1)
                {
                    parts.Add($"department '{department!.Trim()}' matched {result.MatchedDepartments.Count} departments: "
                        + string.Join(", ", result.MatchedDepartments) + ". Tell the user, or ask which one they mean.");
                }
            }
        }

        if (truncated)
        {
            parts.Add("Projector's resource export hit its row cap, so the list and the groups may be incomplete.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static bool Matches(string? filter, params string?[] values) =>
        string.IsNullOrWhiteSpace(filter)
        || values.Any(v => v is not null && v.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ResourceGroupCountDto> Count(
        IEnumerable<ExportedResourceRow> rows, Func<ExportedResourceRow, string?> key) =>
        rows.GroupBy(r => string.IsNullOrWhiteSpace(key(r)) ? "(none)" : key(r)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ResourceGroupCountDto(g.Key, g.Count()))
            .OrderByDescending(g => g.People)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> Distinct(
        IEnumerable<ExportedResourceRow> rows, Func<ExportedResourceRow, string?> key, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? []
            : rows.Select(key).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
}
