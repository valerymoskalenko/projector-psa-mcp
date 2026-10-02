using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Projector.Domain.Exceptions;
using Projector.Domain.Reports;

namespace Projector.ApiClient.Xml;

/// <summary>Response parsers for get_report: report output (CSV), report runs and the legacy export rows.</summary>
public static class ProjectorReportParsers
{
    /// <summary>GetReportStatus without a UID returns this instead of a real output UID.</summary>
    private const string NoUid = "-9223372036854775808";

    /// <summary>
    /// A legacy method reports failure in a normal response: <c>Result</c> other than Ok and an <c>Errors</c> list.
    /// </summary>
    public static void ThrowIfOpsError(XDocument response, string method)
    {
        var result = XmlNodeHelpers.Value(XmlNodeHelpers.LocalNode(response, method + "Result"), "Result");
        if (result is null || string.Equals(result, "Ok", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var error = XmlNodeHelpers.LocalNode(response, "Error");
        var code = XmlNodeHelpers.Value(error, "ErrorCode") ?? result;
        var text = XmlNodeHelpers.Value(error, "ErrorMessage") ?? XmlNodeHelpers.Value(error, "AdditionalInfo");
        throw new ProjectorApiException(text ?? $"Projector call {method} failed ({code}).", code);
    }

    public static int RowCount(XDocument response) =>
        XmlNodeHelpers.NullableInt(response.Root, "RowCount") ?? 0;

    /// <summary>Each <paramref name="rowElement"/> as element name → text; nested lists and empty elements are left out.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ParseRows(XDocument response, string rowElement) =>
        XmlNodeHelpers.LocalNodes(response, rowElement)
            .Select(row => (IReadOnlyDictionary<string, string?>)row.Elements()
                .Where(e => !e.HasElements && !string.IsNullOrEmpty(e.Value))
                .GroupBy(e => e.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (string?)g.First().Value, StringComparer.Ordinal))
            .ToList();

    public static BatchPage ParseBatchPage(XDocument response, string rowElement) =>
        new(
            XmlNodeHelpers.Value(response.Root, "BatchStatus") ?? "None",
            XmlNodeHelpers.NullableInt(response.Root, "RowCount") ?? -1,
            ParseRows(response, rowElement));

    public static IReadOnlyList<ReportRun> ParseReportRuns(XDocument response) =>
        XmlNodeHelpers.LocalNodes(response, "ReportOutput")
            .Select(run =>
            {
                var uid = XmlNodeHelpers.Value(run, "ReportOutputUid");
                return new ReportRun(
                    uid == NoUid ? null : uid,
                    XmlNodeHelpers.Value(run, "ReportName"),
                    XmlNodeHelpers.Value(run, "ReportStatus") ?? "Unknown",
                    Timestamp(XmlNodeHelpers.Value(run, "RequestedTimestamp")),
                    Timestamp(XmlNodeHelpers.Value(run, "CompletedTimestamp")));
            })
            .ToList();

    public static string? SubmittedOutputUid(XDocument response) =>
        XmlNodeHelpers.Value(response.Root, "ReportOutputUid");

    public static string? SubmittedRequestId(XDocument response) =>
        XmlNodeHelpers.Value(response.Root, "RequestId");

    /// <summary>The CSV text of PwsGetReportOutput (<c>ReportData</c>) as a table; the first record is the header.</summary>
    public static ReportTable ParseReportOutput(XDocument response) =>
        ParseCsv(XmlNodeHelpers.LocalNode(response, "ReportData")?.Value);

    /// <summary>
    /// Comma-separated text with quoted fields: a quoted field can hold commas, doubled quotes and line breaks
    /// (time card descriptions do). An empty cell becomes null; short rows are padded to the header.
    /// </summary>
    public static ReportTable ParseCsv(string? csv)
    {
        var records = new List<List<string>>();
        if (!string.IsNullOrEmpty(csv))
        {
            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < csv.Length; i++)
            {
                var c = csv[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"' && field.Length == 0)
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    record.Add(field.ToString());
                    field.Clear();
                }
                else if (c is '\n' or '\r')
                {
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    {
                        i++;
                    }

                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                }
                else
                {
                    field.Append(c);
                }
            }

            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }
        }

        // A trailing line break leaves no record; a blank line in between is one empty field.
        records.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
        if (records.Count == 0)
        {
            return new ReportTable([], []);
        }

        var header = records[0].Select(h => h.Trim()).ToList();
        var rows = records.Skip(1)
            .Select(r => Enumerable.Range(0, header.Count)
                .Select(i => i < r.Count && r[i].Length > 0 ? r[i] : null)
                .ToArray())
            .ToList();
        return new ReportTable(header, rows);
    }

    private static DateTimeOffset? Timestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
}
