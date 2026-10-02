using System.Globalization;
using Projector.Domain.Reports;

namespace Projector.Application.Tools;

/// <summary>
/// Turns raw Ginsu export rows into rows an agent can use. Projector splits one item into an hours row, a revenue
/// row with zero minutes and, for internal or zero-rate work, a row where every number is zero (probed 2026-10-02:
/// 168 of 1,231). <see cref="Clean"/> drops the empty rows and adds up the rest per item; <see cref="Group"/> then
/// adds up over the columns the agent chose.
/// </summary>
public static class GinsuCleaner
{
    /// <summary>A text column: its name in the answer, the Projector field, and the words for Projector's one-letter codes.</summary>
    public sealed record Dimension(string Name, string Field, IReadOnlyDictionary<string, string>? Codes = null);

    /// <summary>A number that is added up; <see cref="Divisor"/> 60 turns minutes into hours.</summary>
    public sealed record Measure(string Name, string Field, double Divisor = 1);

    public static readonly Dimension[] Dimensions =
    [
        new("period", "PeriodStartDate"),
        new("person", "ResourceDisplayName"),
        new("person_id", "ResourceEmployeeId"),
        new("engagement_code", "EngagementCode"),
        new("project_code", "ProjectCode"),
        new("role", "Role"),
        new("task", "ProjectTaskName"),
        new("task_type", "ProjectTaskTypeName"),
        new("rate_type", "ProjectRateTypeName"),
        new("kind", "ActualProjectedFlag", new Dictionary<string, string> { ["A"] = "actual", ["P"] = "planned" }),
        new("status", "HoursStatus", new Dictionary<string, string>
        {
            ["A"] = "approved", ["U"] = "unapproved", ["B"] = "booked", ["R"] = "requested", ["X"] = "unscheduled_revenue"
        }),
        new("hours_type", "HoursType", new Dictionary<string, string> { ["P"] = "project", ["T"] = "time_off", ["H"] = "holiday" }),
        new("billing_status", "BillingStatus", new Dictionary<string, string>
        {
            ["I"] = "issued", ["D"] = "draft", ["U"] = "unbilled", ["P"] = "projected", ["T"] = "time_off",
            ["H"] = "holiday", ["N"] = "nonbillable", ["X"] = "unscheduled_revenue"
        }),
        new("time_off_reason", "TimeoffReasonName"),
        new("holiday", "HolidayName"),
        new("cost_center", "CostCenterName"),
        new("location", "LocationName"),
        new("department", "DepartmentName"),
        new("title", "TitleName"),
        new("currency", "ReportingCurrencyCode")
    ];

    public static readonly Measure[] Measures =
    [
        new("hours", "PersonMinutes", 60),
        new("system_revenue", "SystemRevenue"),
        new("contract_revenue", "ContractRevenue"),
        new("billing_adjusted_revenue", "BillingAdjustedRevenue"),
        new("recognized_revenue", "RecognizedRevenue"),
        new("standard_revenue", "StandardRevenue"),
        new("resource_direct_cost", "ResourceDirectCost"),
        new("other_direct_costs", "OtherDirectCosts")
    ];

    public static readonly string[] DefaultColumns = ["period", "person", "project_code", "kind", "status", "hours"];

    /// <summary>The measure added when the agent chose only text columns.</summary>
    public const string DefaultMeasure = "hours";

    public static bool IsMeasure(string column) => Measures.Any(m => m.Name == column);

    /// <summary>
    /// One row per item at full detail (every dimension, every measure): rows where all measures are zero are
    /// dropped, rows of the same item (its hours row and its revenue row) are added up.
    /// </summary>
    public static ReportTable Clean(IEnumerable<IReadOnlyDictionary<string, string?>> rawRows)
    {
        var sums = new Dictionary<string, (string?[] Dims, double[] Values)>(StringComparer.Ordinal);
        foreach (var raw in rawRows)
        {
            var values = Measures.Select(m => Number(raw.GetValueOrDefault(m.Field)) / m.Divisor).ToArray();
            if (values.All(v => v == 0))
            {
                continue;
            }

            var dims = Dimensions.Select(d => Label(d, raw.GetValueOrDefault(d.Field))).ToArray();
            Add(sums, dims, values);
        }

        // Kept at six decimals, so adding up 20-minute cards later does not drift.
        return ToTable(Dimensions.Select(d => d.Name).Concat(Measures.Select(m => m.Name)).ToList(), sums, digits: 6);
    }

    /// <summary>
    /// The clean table added up over <paramref name="columns"/>: text columns form the groups, measure columns are
    /// summed (<see cref="DefaultMeasure"/> when none is chosen). Groups where the chosen numbers are all zero are
    /// left out (e.g. revenue-only items when only hours were asked for). Sorted by the text columns.
    /// </summary>
    public static ReportTable Group(ReportTable clean, IReadOnlyList<string> columns)
    {
        var dims = columns.Where(c => !IsMeasure(c)).ToList();
        var measures = columns.Where(IsMeasure).ToList();
        if (measures.Count == 0)
        {
            measures.Add(DefaultMeasure);
        }

        var dimIndex = dims.Select(d => IndexOf(clean, d)).ToArray();
        var measureIndex = measures.Select(m => IndexOf(clean, m)).ToArray();
        var sums = new Dictionary<string, (string?[] Dims, double[] Values)>(StringComparer.Ordinal);
        foreach (var row in clean.Rows)
        {
            var values = measureIndex.Select(i => Number(row[i])).ToArray();
            if (values.All(v => v == 0))
            {
                continue;
            }

            Add(sums, dimIndex.Select(i => row[i]).ToArray(), values);
        }

        return ToTable(dims.Concat(measures).ToList(), sums, digits: 2);
    }

    private static void Add(Dictionary<string, (string?[] Dims, double[] Values)> sums, string?[] dims, double[] values)
    {
        var key = string.Join('\u001f', dims);
        if (sums.TryGetValue(key, out var existing))
        {
            for (var i = 0; i < values.Length; i++)
            {
                existing.Values[i] += values[i];
            }
        }
        else
        {
            sums[key] = (dims, values);
        }
    }

    private static ReportTable ToTable(
        IReadOnlyList<string> columns, Dictionary<string, (string?[] Dims, double[] Values)> sums, int digits)
    {
        var rows = sums
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => s.Value.Dims
                .Concat(s.Value.Values.Select(v => (string?)Math.Round(v, digits).ToString(CultureInfo.InvariantCulture)))
                .ToArray())
            .ToList();
        return new ReportTable(columns, rows);
    }

    private static int IndexOf(ReportTable table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i] == column)
            {
                return i;
            }
        }

        throw new ArgumentException($"Unknown column '{column}'.");
    }

    private static string? Label(Dimension dimension, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (dimension.Codes is not null)
        {
            return dimension.Codes.GetValueOrDefault(value, value);
        }

        // A period is a date: keep yyyy-MM-dd.
        return dimension.Name == "period" && value.Length > 10 ? value[..10] : value;
    }

    private static double Number(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
