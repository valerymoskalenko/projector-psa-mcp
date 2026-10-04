using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Projector.ApiClient.Xml;

/// <summary>One Projector call as measured by <see cref="ProjectorCallStatsHandler"/>.</summary>
/// <param name="Action">The SOAP method, e.g. PwsGetTimeCards.</param>
/// <param name="DurationMs">Until the whole response was read, retries included.</param>
/// <param name="ResponseBytes">Size of the response body; null when there was no response.</param>
/// <param name="Rows">Records in the response (see <see cref="ProjectorCallStats.CountRows"/>); null when unknown.</param>
/// <param name="Status">The HTTP status code, or <c>exception:&lt;Type&gt;</c>.</param>
public sealed record ProjectorCallStat(string Action, long DurationMs, long? ResponseBytes, int? Rows, string Status);

/// <summary>
/// Collects the Projector calls made during one tool call, so the tool call's own log line can carry their count,
/// time, size and rows. The collector is ambient (it follows the async flow, parallel calls included); without one
/// the calls are only logged.
/// </summary>
public static class ProjectorCallStats
{
    private static readonly AsyncLocal<Collector?> Ambient = new();

    /// <summary>The records of the response per SOAP method: the element that repeats once per returned row.</summary>
    private static readonly Dictionary<string, string[]> RowElements = new(StringComparer.Ordinal)
    {
        ["PwsGetResourceList"] = ["PwsResourceSummary"],
        ["PwsGetUserList"] = ["PwsUserSummaryElement"],
        ["PwsGetTimeCards"] = ["PwsTimecardDetail", "PwsTimeOffCardDetail"],
        ["PwsGetEngagementList"] = ["PwsEngagementSummary"],
        ["PwsGetEngagement"] = ["PwsEngagementElement"],
        ["PwsGetProject"] = ["PwsProjectElement"],
        ["PwsGetProjectRoles"] = ["PwsProjectRoleElement"],
        ["PwsGetResourceSchedulingRoleData"] = ["PwsProjectRoleSchedule"],
        ["PwsGetResourceSchedule"] = ["PwsScheduleDate"],
        ["PwsGetResourcePto"] = ["PwsHolidayPto"],
        ["PwsSearchProjects"] = ["PwsProjectDescriptor"],
        ["PwsGetTimeEntryProjectRole"] = ["PwsProjectTask"],
        ["PwsGetExpenseReports"] = ["PwsExpenseDocument"],
        ["PwsGetExpenseDocument"] = ["PwsCostCardElement"],
        ["PwsSaveExpenseDocument"] = ["PwsCostCardElement"],
        ["PwsGetResourceExpenseEntryInfo"] = ["PwsProjectInfoForResourceExpenseEntry"],
        ["PwsGetFolderContents"] = ["PwsDocument"],
        ["ExportResources"] = ["Resource"],
        ["ExportScheduledTimeoff"] = ["ScheduledTimeoff"],
        ["ExportProjectList"] = ["Project"],
        ["ExportTimeCards"] = ["TimeCard"],
        ["ExportOlapGinsuRecords"] = ["OlapGinsuRecord"],
        ["GetReportStatus"] = ["ReportOutput"]
    };

    /// <summary>Starts collecting for the current async flow (one tool call).</summary>
    public static Collector Begin()
    {
        var collector = new Collector();
        Ambient.Value = collector;
        return collector;
    }

    internal static void Record(ProjectorCallStat stat) => Ambient.Value?.Add(stat);

    /// <summary>Bytes as KB with one decimal, the unit of the log lines.</summary>
    public static double? Kb(long? bytes) => bytes is null ? null : Math.Round(bytes.Value / 1024.0, 1);

    /// <summary>
    /// Rows in a response: how many times the method's record element closes (<c>&lt;/a:PwsTimecardDetail&gt;</c>).
    /// Counted on the text, without parsing; null for a method that returns one item or is not listed.
    /// </summary>
    public static int? CountRows(string action, string? xml)
    {
        if (string.IsNullOrEmpty(xml) || !RowElements.TryGetValue(action, out var names))
        {
            return null;
        }

        return names.Sum(name => CountClosingTags(xml, name));
    }

    private static int CountClosingTags(string xml, string localName)
    {
        var count = 0;
        var tail = localName + ">";
        for (var at = xml.IndexOf(tail, StringComparison.Ordinal); at >= 0; at = xml.IndexOf(tail, at + tail.Length, StringComparison.Ordinal))
        {
            // "</Name>" or "</prefix:Name>": walk back over the prefix to "</".
            var i = at - 1;
            if (i >= 0 && xml[i] == ':')
            {
                i--;
                while (i >= 0 && (char.IsLetterOrDigit(xml[i]) || xml[i] is '_' or '-' or '.'))
                {
                    i--;
                }
            }

            if (i >= 1 && xml[i] == '/' && xml[i - 1] == '<')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The Projector calls of one tool call.</summary>
    public sealed class Collector
    {
        private readonly List<ProjectorCallStat> _calls = [];

        internal void Add(ProjectorCallStat stat)
        {
            lock (_calls)
            {
                _calls.Add(stat);
            }
        }

        public IReadOnlyList<ProjectorCallStat> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }
    }
}

/// <summary>
/// Measures every Projector call: one log line (category <see cref="LoggerCategory"/>) with the SOAP method, duration,
/// response size and rows, and the same numbers for the tool call's totals (<see cref="ProjectorCallStats"/>).
/// It sits outside the retry handler, so it sees one logical call (each attempt is still its own dependency in
/// App Insights), and reading the body here is the read the HttpClient would do anyway.
/// No request or response contents are logged.
/// </summary>
public sealed class ProjectorCallStatsHandler : DelegatingHandler
{
    public const string LoggerCategory = "Projector.Mcp.ProjectorCall";

    private readonly ILogger _logger;

    public ProjectorCallStatsHandler(ILoggerFactory loggers)
    {
        _logger = loggers.CreateLogger(LoggerCategory);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A call without a SOAP action (the receipt upload) is named by the last segment of its URL.
        var action = request.Headers.TryGetValues("SOAPAction", out var values) && values.FirstOrDefault() is { Length: > 0 } header
            ? ProjectorSoapHttp.SoapActionName(header)
            : request.RequestUri?.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } segment ? segment : "unknown";
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        string body;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            // Buffers the body, so the caller's own read is served from memory.
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Report(new ProjectorCallStat(action, ElapsedMs(started), null, null, "exception:" + ex.GetType().Name));
            throw;
        }

        var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body);
        Report(new ProjectorCallStat(
            action,
            ElapsedMs(started),
            bytes,
            ProjectorCallStats.CountRows(action, body),
            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return response;
    }

    private void Report(ProjectorCallStat stat)
    {
        ProjectorCallStats.Record(stat);
        _logger.LogInformation(
            "Projector call {ProjectorAction} {ProjectorStatus} in {ProjectorMs} ms, {ResponseKb} KB, {ResponseRows} rows",
            stat.Action,
            stat.Status,
            stat.DurationMs,
            ProjectorCallStats.Kb(stat.ResponseBytes),
            stat.Rows);
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
