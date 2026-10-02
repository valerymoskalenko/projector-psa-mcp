using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Projector.Application.Auth;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Reports;

namespace Projector.Application.Tools;

/// <summary>
/// get_report: large, cross-person results from the signed-in user's own Projector session. Four datasets:
/// a saved report (by web service code, spec UID or output UID), the Ginsu batch export (hours and revenue by
/// person, project and period), the project list and approved time cards.
/// <para>
/// Paging: projects and time_cards continue with Projector's own "after" values, and nothing is kept here.
/// A saved report (Projector returns it whole) and a Ginsu batch (the clean-up needs every row) are kept for
/// ten minutes per user in <see cref="ReportCache"/>; a missing entry is read from Projector again.
/// </para>
/// </summary>
public sealed class ReportToolService
{
    public const string LoggerCategory = "Projector.Mcp.Report";

    public const int DefaultMaxRows = 100;
    public const int MaxMaxRows = 500;

    /// <summary>Longest range for the date-driven datasets.</summary>
    public const int MaxDateSpanDays = 366;

    /// <summary>
    /// A Ginsu batch with more raw rows is refused: reading it takes about a second per 1,000 rows, inside one
    /// tool call.
    /// </summary>
    public const int MaxGinsuRawRows = 20000;

    private const int GinsuPageRows = 1000;

    /// <summary>A batch this large is read in a call of its own when the wait for it already took long.</summary>
    private const int LargeBatchRows = 5000;

    /// <summary>How long one call waits for a report run or an export batch before it answers "running".</summary>
    internal static TimeSpan WaitBudget { get; set; } = TimeSpan.FromSeconds(20);

    internal static TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorReportClient _client;
    private readonly ReportCache _cache;
    private readonly ILogger _logger;

    public ReportToolService(
        ProjectorConnectionService connections,
        IProjectorReportClient client,
        ReportCache cache,
        ILoggerFactory loggers)
    {
        _connections = connections;
        _client = client;
        _cache = cache;
        _logger = loggers.CreateLogger(LoggerCategory);
    }

    /// <summary>What happened inside one call, for the log line. No contents, codes or names.</summary>
    private sealed class CallStats
    {
        public string Dataset = "catalog";
        public string Address = "none";
        public bool CacheHit;
        public long WaitMs;
        public int Pages;
        public int RawRows;
        public int CleanRows;
        public string Outcome = "ok";
    }

    public async Task<object> GetReportAsync(string connectionId, ReportRequest request, CancellationToken ct)
    {
        var connection = await _connections.RequireConnectionAsync(connectionId, requiredScope: null, ct);
        var stats = new CallStats();
        try
        {
            var answer = await WithRefreshAsync(connection, c => RunAsync(c, request, stats, ct), ct);
            // The serializer leaves out null properties, but not null dictionary values.
            foreach (var key in answer.Where(a => a.Value is null).Select(a => a.Key).ToList())
            {
                answer.Remove(key);
            }

            if (answer.GetValueOrDefault("status") is "running")
            {
                stats.Outcome = "running";
            }

            return answer;
        }
        catch (Exception ex)
        {
            stats.Outcome = ex is ProjectorApiException api ? api.ErrorCode ?? "projector_error" : ex.GetType().Name;
            throw;
        }
        finally
        {
            _logger.LogInformation(
                "get_report {ReportDataset} by {ReportAddress}: cache hit {ReportCacheHit}, waited {ReportWaitMs} ms, " +
                "{ReportPages} Projector page(s), {ReportRawRows} raw rows, {ReportCleanRows} rows after clean-up, {ReportOutcome}",
                stats.Dataset, stats.Address, stats.CacheHit, stats.WaitMs, stats.Pages, stats.RawRows, stats.CleanRows, stats.Outcome);
        }
    }

    private Task<Dictionary<string, object?>> RunAsync(
        ProjectorConnection connection, ReportRequest request, CallStats stats, CancellationToken ct)
    {
        var position = new ReportPosition();
        if (request.Cursor is { } cursor && !string.IsNullOrWhiteSpace(cursor))
        {
            (request, position) = ReportCursor.Decode(cursor, connection.UserKey);
            stats.Address = "cursor";
        }

        request = request with
        {
            Dataset = request.Dataset?.Trim().ToLowerInvariant(),
            Query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
            Columns = request.Columns?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToArray() is { Length: > 0 } columns
                ? columns
                : null,
            MaxRows = Math.Clamp(request.MaxRows <= 0 ? DefaultMaxRows : request.MaxRows, 1, MaxMaxRows),
            Cursor = null
        };
        stats.Dataset = request.Dataset ?? "catalog";

        return request.Dataset switch
        {
            null or "" => CatalogAsync(connection, ct),
            ReportDatasets.Report => ReportAsync(connection, request, position, stats, ct),
            ReportDatasets.Ginsu => GinsuAsync(connection, request, position, stats, ct),
            ReportDatasets.Projects => ProjectsAsync(connection, request, position, stats, ct),
            ReportDatasets.TimeCards => TimeCardsAsync(connection, request, position, stats, ct),
            _ => throw new ArgumentException(
                $"Unknown dataset '{request.Dataset}'. Use one of: {string.Join(", ", ReportDatasets.Names)}; " +
                "call get_report without arguments to see what each one answers.")
        };
    }

    // ---- catalog ----

    private async Task<Dictionary<string, object?>> CatalogAsync(ProjectorConnection connection, CancellationToken ct)
    {
        var (runs, note) = await RecentRunsAsync(connection, ct);
        return new Dictionary<string, object?>
        {
            ["datasets"] = ReportDatasets.Catalog(),
            ["recent_report_runs"] = runs,
            ["note"] = note ?? "recent_report_runs lists the user's report runs of the last days by name; to read one, " +
                "give its web service code (dataset report, code)."
        };
    }

    /// <summary>The user's recent report runs by name (the list carries no ids); a failure becomes a note.</summary>
    private async Task<(IReadOnlyList<object>? Runs, string? Note)> RecentRunsAsync(ProjectorConnection connection, CancellationToken ct)
    {
        try
        {
            var runs = await _client.GetReportStatusAsync(connection, outputUid: null, ct);
            return (runs
                .OrderByDescending(r => r.Completed ?? r.Requested)
                .Take(20)
                .Select(r => (object)new { name = r.Name, status = r.Status, completed = Time(r.Completed) })
                .ToList(), null);
        }
        catch (ProjectorApiException ex)
        {
            return (null, $"The list of recent report runs is not available ({ex.ErrorCode ?? "Projector error"}).");
        }
    }

    // ---- saved report ----

    private async Task<Dictionary<string, object?>> ReportAsync(
        ProjectorConnection connection, ReportRequest request, ReportPosition position, CallStats stats, CancellationToken ct)
    {
        var code = Blank(request.Code);
        var specUid = Uid(request.SpecUid, "spec_uid");
        var outputUid = Uid(request.OutputUid, "output_uid");
        if (new[] { code, specUid, outputUid }.Count(v => v is not null) != 1)
        {
            throw new ArgumentException(
                "Dataset 'report' needs exactly one of: code (the report's web service code, set on its Output tab in " +
                "Projector: returns the latest run), spec_uid (Additional Actions > Show Report Spec UID: runs the " +
                "report now) or output_uid (one finished run).");
        }

        if (stats.Address != "cursor")
        {
            stats.Address = code is not null ? "code" : specUid is not null ? "spec_uid" : "output_uid";
        }

        ReportRun? run = null;
        if (specUid is not null)
        {
            // A spec UID always means a fresh run; the cursor carries the run while it is not finished.
            var runId = position.RunId ?? await _client.SubmitReportSpecAsync(connection, specUid, ct);
            run = await WaitAsync(stats, ct, async () =>
            {
                var found = (await _client.GetReportStatusAsync(connection, runId, ct)).FirstOrDefault();
                return (found, found is null || found.Status is "Queued" or "Running");
            });
            if (run is null || run.Status is "Queued" or "Running")
            {
                return Running(connection, request, position with { RunId = runId }, ReportDatasets.Report,
                    "The report is still running in Projector. Call get_report again in a few seconds with only the argument cursor set to next_cursor.");
            }

            if (run.Status == "Empty")
            {
                return Empty(ReportDatasets.Report, "PwsGetReportOutput", run.Completed,
                    "The report ran and returned no rows.");
            }

            if (run.Status != "Completed")
            {
                throw new ProjectorApiException($"The report run ended with status {run.Status} in Projector.", "report_run_failed");
            }

            // From here on it is a finished run: later pages address it by its output UID.
            outputUid = runId;
            request = request with { SpecUid = null, OutputUid = runId };
        }

        var key = outputUid is not null ? "report::uid::" + outputUid : "report::code::" + code!.ToLowerInvariant();
        if (_cache.TryGet<CachedReport>(connection, key, out var cached))
        {
            stats.CacheHit = true;
        }
        else
        {
            ReportTable table;
            try
            {
                table = await _client.GetReportOutputAsync(connection, code, outputUid, ct);
            }
            catch (ProjectorApiException ex) when (string.Equals(ex.ErrorCode, "EntityNotFound", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectorApiException(
                    "Projector found no report output for this " + (code is not null ? "web service code" : "output UID") +
                    ". Either it does not exist, the report has not run in the last days (output is kept 7 days by " +
                    "default), or you are neither its owner nor on its distribution list. Ask the user to run the " +
                    "report in Projector, or to check the code on the report's Output tab.",
                    "report_not_found",
                    ex);
            }

            stats.Pages++;
            DateTimeOffset? asOf = null;
            if (outputUid is not null)
            {
                run ??= (await _client.GetReportStatusAsync(connection, outputUid, ct)).FirstOrDefault();
                asOf = run?.Completed;
            }

            cached = new CachedReport(table, asOf);
            _cache.Set(connection, key, cached, ReportCache.EstimateBytes(table));
        }

        stats.RawRows = cached!.Table.Rows.Count;
        var shaped = SelectColumns(cached.Table, request.Columns);
        var answer = Page(connection, request, position, ReportDatasets.Report, "PwsGetReportOutput", cached.DataAsOf,
            shaped, numeric: null, stats);
        if (position.Offset == 0)
        {
            answer["available_columns"] = cached.Table.Columns;
            if (code is not null)
            {
                // CSV carries no run time, and a code has no output UID to ask the status for.
                var (runs, _) = await RecentRunsAsync(connection, ct);
                answer["recent_report_runs"] = runs;
                answer["note"] = Join(answer.GetValueOrDefault("note") as string,
                    "The data is the report's last run; its time is not part of the output. recent_report_runs shows " +
                    "when the user's reports last ran, by name.");
            }
        }

        return answer;
    }

    // ---- Ginsu batch export ----

    private async Task<Dictionary<string, object?>> GinsuAsync(
        ProjectorConnection connection, ReportRequest request, ReportPosition position, CallStats stats, CancellationToken ct)
    {
        var (start, end) = DateRange(request, ReportDatasets.Ginsu);
        var today = DateTime.UtcNow.Date;
        var cutoff = Blank(request.CutoffDate) is { } given
            ? ParseDate(given, "cutoff_date")
            : end < today ? end : today;
        var bucket = (Blank(request.Bucket) ?? "month").ToLowerInvariant() switch
        {
            "day" or "d" => "D",
            "week" or "w" => "W",
            "month" or "m" => "M",
            "quarter" or "q" => "Q",
            "year" or "y" => "Y",
            "none" => "",
            var other => throw new ArgumentException($"bucket '{other}' is not valid. Use day, week, month, quarter, year or none.")
        };
        var by = (Blank(request.By) ?? "projects").ToLowerInvariant();
        if (by is not ("projects" or "resources"))
        {
            throw new ArgumentException("by must be 'projects' (cost_center filters engagements) or 'resources' (it filters people).");
        }

        var columns = GinsuColumns(request.Columns);
        // Projector returns time off only when the cost center filters resources.
        var export = new GinsuExportRequest(
            Iso(start), Iso(end), Iso(cutoff), bucket, Blank(request.CostCenter),
            FilterByResources: by == "resources" || request.IncludeTimeOff,
            request.BillableOnly, request.IncludeTimeOff, request.IncludeUnapproved);
        if (stats.Address != "cursor")
        {
            stats.Address = "dates";
        }

        var key = "ginsu::" + string.Join('|', export.BeginDate, export.EndDate, export.CutoffDate, export.BucketWidth,
            export.CostCenterNumber, export.FilterByResources, export.BillableOnly, export.IncludeTimeOff, export.IncludeUnapproved);
        if (_cache.TryGet<CachedReport>(connection, key, out var cached))
        {
            stats.CacheHit = true;
        }
        else
        {
            var runId = position.RunId ?? await _client.SubmitGinsuExportAsync(connection, export, ct);
            var batch = await WaitForBatchAsync(connection, runId, stats, ct);
            if (batch.Status is "Deleted" or "None" && position.RunId is not null)
            {
                // Projector deletes a batch soon after it completes: the cursor outlived it, so run it again.
                runId = await _client.SubmitGinsuExportAsync(connection, export, ct);
                batch = await WaitForBatchAsync(connection, runId, stats, ct);
            }

            if (batch.IsWaiting)
            {
                return Running(connection, request, position with { RunId = runId }, ReportDatasets.Ginsu,
                    "Projector is still preparing the data (its export queue can take from seconds to a few minutes). " +
                    "Call get_report again in about 20 seconds with only the argument cursor set to next_cursor.");
            }

            if (batch.Status == "Completed" && batch.RowCount > LargeBatchRows && stats.WaitMs > WaitBudget.TotalMilliseconds / 2)
            {
                // Waiting already used much of this call; reading a large batch on top could exceed the client's
                // tool timeout. The batch is ready, so the next call reads it at once.
                return Running(connection, request, position with { RunId = runId }, ReportDatasets.Ginsu,
                    "The data is ready in Projector. Call get_report again now with only the argument cursor set to next_cursor to read it.");
            }

            var asOf = DateTimeOffset.UtcNow;
            ReportTable clean;
            if (batch.Status == "Empty")
            {
                clean = GinsuCleaner.Clean([]);
            }
            else if (batch.Status != "Completed")
            {
                throw new ProjectorApiException($"The Projector export ended with status {batch.Status}. Call get_report again to rerun it.", "export_failed");
            }
            else if (batch.RowCount > MaxGinsuRawRows)
            {
                throw new ProjectorApiException(
                    $"This range gives {batch.RowCount} rows in Projector, more than get_report reads in one call " +
                    $"({MaxGinsuRawRows}). Use a larger bucket (month, quarter, none), a shorter date range or a cost_center.",
                    "too_many_rows");
            }
            else
            {
                var raw = new List<IReadOnlyDictionary<string, string?>>(batch.RowCount);
                long after = 0;
                while (true)
                {
                    var page = await _client.GetGinsuRecordsAsync(connection, runId, after, GinsuPageRows, onlyCount: false, ct);
                    stats.Pages++;
                    raw.AddRange(page.Rows);
                    if (page.Rows.Count < GinsuPageRows || raw.Count > MaxGinsuRawRows
                        || !long.TryParse(page.Rows[^1].GetValueOrDefault("RowIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out after))
                    {
                        break;
                    }
                }

                stats.RawRows = raw.Count;
                clean = GinsuCleaner.Clean(raw);
            }

            cached = new CachedReport(clean, asOf);
            _cache.Set(connection, key, cached, ReportCache.EstimateBytes(clean));
        }

        var grouped = GinsuCleaner.Group(cached!.Table, columns);
        var numeric = grouped.Columns.Where(GinsuCleaner.IsMeasure).ToHashSet(StringComparer.Ordinal);
        var answer = Page(connection, request, position with { RunId = null }, ReportDatasets.Ginsu, "SubmitOlapGinsuExport",
            cached.DataAsOf, grouped, numeric, stats);
        if (position.Offset == 0)
        {
            answer["available_columns"] = cached.Table.Columns;
            answer["note"] = Join(answer.GetValueOrDefault("note") as string,
                $"Rows are added up over the chosen columns. Days up to {Iso(cutoff)} are actual (posted) hours, later " +
                "days planned (booked) hours. project_code is a code: dataset projects gives the names.");
        }

        return answer;
    }

    private Task<BatchPage> WaitForBatchAsync(ProjectorConnection connection, string runId, CallStats stats, CancellationToken ct) =>
        WaitAsync(stats, ct, async () =>
        {
            var page = await _client.GetGinsuRecordsAsync(connection, runId, 0, GinsuPageRows, onlyCount: true, ct);
            return (page, page.IsWaiting);
        });

    private static IReadOnlyList<string> GinsuColumns(string[]? asked)
    {
        if (asked is null)
        {
            return GinsuCleaner.DefaultColumns;
        }

        var known = GinsuCleaner.Dimensions.Select(d => d.Name).Concat(GinsuCleaner.Measures.Select(m => m.Name)).ToList();
        return asked.Select(a => Known(a, known)).Distinct(StringComparer.Ordinal).ToList();
    }

    // ---- project list ----

    private async Task<Dictionary<string, object?>> ProjectsAsync(
        ProjectorConnection connection, ReportRequest request, ReportPosition position, CallStats stats, CancellationToken ct)
    {
        if (stats.Address != "cursor")
        {
            stats.Address = "list";
        }

        var columns = ExportColumns(request.Columns, ReportDatasets.ProjectColumns, ReportDatasets.ProjectDefaultColumns);
        var openOnly = !request.IncludeClosed;
        var total = position.Total
            ?? (await _client.ExportProjectListAsync(connection, openOnly, null, request.MaxRows, onlyCount: true, ct)).RowCount;
        var page = await _client.ExportProjectListAsync(connection, openOnly, position.After, request.MaxRows, onlyCount: false, ct);
        stats.Pages++;
        var next = page.Rows.Count >= request.MaxRows && page.Rows[^1].GetValueOrDefault("ProjectCode") is { } last
            ? position with { After = last, Total = total }
            : null;
        return await ExportAnswerAsync(connection, request, ReportDatasets.Projects, "ExportProjectList", columns, page, total, next,
            openOnly ? "Only projects open for time are listed; include_closed = true lists every project." : null, stats, ct);
    }

    // ---- approved time cards ----

    private async Task<Dictionary<string, object?>> TimeCardsAsync(
        ProjectorConnection connection, ReportRequest request, ReportPosition position, CallStats stats, CancellationToken ct)
    {
        var (start, end) = DateRange(request, ReportDatasets.TimeCards);
        if (stats.Address != "cursor")
        {
            stats.Address = "dates";
        }

        var columns = ExportColumns(request.Columns, ReportDatasets.TimeCardColumns, ReportDatasets.TimeCardDefaultColumns);
        var total = position.Total
            ?? (await _client.ExportTimeCardsAsync(connection, Iso(start), Iso(end), null, null, request.MaxRows, onlyCount: true, ct)).RowCount;
        var page = await _client.ExportTimeCardsAsync(
            connection, Iso(start), Iso(end), position.After, position.After2, request.MaxRows, onlyCount: false, ct);
        stats.Pages++;
        var next = page.Rows.Count >= request.MaxRows
            && page.Rows[^1].GetValueOrDefault("ApprovedTimestamp") is { } approved
            && page.Rows[^1].GetValueOrDefault("ReferenceSystemId") is { } id
                ? position with { After = approved, After2 = id, Total = total }
                : null;
        return await ExportAnswerAsync(connection, request, ReportDatasets.TimeCards, "ExportTimeCards", columns, page, total, next,
            "Approved time cards only. For hours in every status use dataset ginsu; for one person's cards use list_timecards.",
            stats, ct);
    }

    /// <summary>One Projector page of an export as an answer: chosen columns, names for ids, query inside the page.</summary>
    private async Task<Dictionary<string, object?>> ExportAnswerAsync(
        ProjectorConnection connection,
        ReportRequest request,
        string dataset,
        string source,
        IReadOnlyList<ReportColumn> columns,
        ExportPage page,
        int total,
        ReportPosition? next,
        string? note,
        CallStats stats,
        CancellationToken ct)
    {
        stats.RawRows = page.Rows.Count;
        IReadOnlyDictionary<string, string>? names = null;
        if (columns.Any(c => c.Kind == ReportColumnKind.PersonName) && page.Rows.Count > 0)
        {
            (names, var namesNote) = await ResourceNamesAsync(connection, ct);
            note = Join(note, namesNote);
        }

        var rows = page.Rows.Select(raw => columns.Select(c => Cell(c, raw.GetValueOrDefault(c.Field), names)).ToArray());
        if (request.Query is not null)
        {
            rows = rows.Where(r => TextMatch.Matches(request.Query, r.Select(v => v?.ToString()).ToArray()));
            note = Join(note, "query was applied inside this page; total counts all rows before query. Keep paging while has_more is true.");
        }

        var list = rows.ToList();
        // Projector's exports name a person only by the resource reference id; people without one come back blank.
        var personAt = columns.Select((c, i) => (c, i)).Where(x => x.c.Kind == ReportColumnKind.PersonName).Select(x => x.i).ToList();
        var unnamed = personAt.Count == 0 ? 0 : list.Count(r => personAt.Any(i => r[i] is null));
        if (unnamed > 0 && dataset == ReportDatasets.TimeCards)
        {
            note = Join(note, $"{unnamed} of these cards have no person: Projector's time card export identifies people only by " +
                "their employee id, and these people have none in Projector. The role column often names them; dataset ginsu " +
                "shows everyone by name.");
        }
        stats.CleanRows = list.Count;
        var answer = new Dictionary<string, object?>
        {
            ["dataset"] = dataset,
            ["source"] = source,
            ["status"] = "ok",
            ["data_as_of"] = Time(DateTimeOffset.UtcNow),
            ["count"] = list.Count,
            ["total"] = total,
            ["has_more"] = next is not null
        };
        if (next is not null)
        {
            answer["next_cursor"] = ReportCursor.Encode(connection.UserKey, request, next);
            note = Join(note, "For the next part call get_report with only the argument cursor set to next_cursor.");
        }

        answer["columns"] = columns.Select(c => c.Name).ToList();
        answer["rows"] = list;
        if (note is not null)
        {
            answer["note"] = note;
        }

        return answer;
    }

    /// <summary>Resource id → name, kept ten minutes per user. A user who may not export resources keeps the ids.</summary>
    private async Task<(IReadOnlyDictionary<string, string>? Names, string? Note)> ResourceNamesAsync(
        ProjectorConnection connection, CancellationToken ct)
    {
        const string key = "names";
        if (_cache.TryGet<IReadOnlyDictionary<string, string>>(connection, key, out var names))
        {
            return (names, null);
        }

        try
        {
            names = await _client.ExportResourceNamesAsync(connection, ct);
            _cache.Set(connection, key, names, names.Sum(n => 80L + 2 * (n.Key.Length + n.Value.Length)));
            return (names, null);
        }
        catch (ProjectorApiException)
        {
            return (null, "People are shown by their resource id: Projector did not let this user read the resource list.");
        }
    }

    // ---- shared ----

    /// <summary>Polls until the run leaves the queue or <see cref="WaitBudget"/> is used up; returns the last state.</summary>
    private static async Task<T> WaitAsync<T>(CallStats stats, CancellationToken ct, Func<Task<(T State, bool Waiting)>> poll)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var (state, waiting) = await poll();
                if (!waiting || Stopwatch.GetElapsedTime(started) + PollDelay > WaitBudget)
                {
                    return state;
                }

                await Task.Delay(PollDelay, ct);
            }
        }
        finally
        {
            stats.WaitMs += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    /// <summary>One page of a cached table: query, then offset and max_rows; says how to get the next part.</summary>
    private static Dictionary<string, object?> Page(
        ProjectorConnection connection,
        ReportRequest request,
        ReportPosition position,
        string dataset,
        string source,
        DateTimeOffset? dataAsOf,
        ReportTable table,
        HashSet<string>? numeric,
        CallStats stats)
    {
        var matching = request.Query is null
            ? table.Rows
            : table.Rows.Where(r => TextMatch.Matches(request.Query, r)).ToList();
        stats.CleanRows = matching.Count;
        var offset = Math.Min(position.Offset, matching.Count);
        var numericIndex = table.Columns.Select(c => numeric?.Contains(c) == true).ToArray();
        var rows = matching.Skip(offset).Take(request.MaxRows)
            .Select(r => r.Select((cell, i) => numericIndex[i] && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? (object?)n
                : cell).ToArray())
            .ToList();
        var hasMore = offset + rows.Count < matching.Count;
        var answer = new Dictionary<string, object?>
        {
            ["dataset"] = dataset,
            ["source"] = source,
            ["status"] = matching.Count == 0 ? "empty" : "ok"
        };
        if (dataAsOf is not null)
        {
            answer["data_as_of"] = Time(dataAsOf);
        }

        answer["count"] = rows.Count;
        answer["total"] = matching.Count;
        answer["has_more"] = hasMore;
        if (hasMore)
        {
            answer["next_cursor"] = ReportCursor.Encode(connection.UserKey, request, position with { Offset = offset + rows.Count });
            answer["note"] = $"Rows {offset + 1}-{offset + rows.Count} of {matching.Count}. For the next part call get_report with " +
                "only the argument cursor set to next_cursor, or narrow with query or fewer columns.";
        }

        answer["columns"] = table.Columns;
        answer["rows"] = rows;
        return answer;
    }

    private static Dictionary<string, object?> Running(
        ProjectorConnection connection, ReportRequest request, ReportPosition position, string dataset, string note) =>
        new()
        {
            ["dataset"] = dataset,
            ["status"] = "running",
            ["count"] = 0,
            ["has_more"] = true,
            ["next_cursor"] = ReportCursor.Encode(connection.UserKey, request, position),
            ["note"] = note
        };

    private static Dictionary<string, object?> Empty(string dataset, string source, DateTimeOffset? dataAsOf, string note) =>
        new()
        {
            ["dataset"] = dataset,
            ["source"] = source,
            ["status"] = "empty",
            ["data_as_of"] = Time(dataAsOf),
            ["count"] = 0,
            ["total"] = 0,
            ["has_more"] = false,
            ["note"] = note
        };

    /// <summary>The asked columns of a report, matched by header name without regard to case, spaces or underscores.</summary>
    private static ReportTable SelectColumns(ReportTable table, string[]? asked)
    {
        if (asked is null)
        {
            return table;
        }

        static string Fold(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var index = asked.Select(a =>
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (Fold(table.Columns[i]) == Fold(a))
                {
                    return i;
                }
            }

            throw new ArgumentException($"The report has no column '{a}'. Its columns: {string.Join(", ", table.Columns)}.");
        }).Distinct().ToArray();
        return new ReportTable(
            index.Select(i => table.Columns[i]).ToList(),
            table.Rows.Select(r => index.Select(i => r[i]).ToArray()).ToList());
    }

    private static IReadOnlyList<ReportColumn> ExportColumns(string[]? asked, ReportColumn[] all, string[] defaults)
    {
        var known = all.Select(c => c.Name).ToList();
        return (asked?.Select(a => Known(a, known)) ?? defaults)
            .Distinct(StringComparer.Ordinal)
            .Select(name => all.First(c => c.Name == name))
            .ToList();
    }

    private static string Known(string asked, IReadOnlyList<string> known)
    {
        var name = asked.Trim().ToLowerInvariant().Replace(' ', '_');
        return known.Contains(name)
            ? name
            : throw new ArgumentException($"Unknown column '{asked}'. Columns of this dataset: {string.Join(", ", known)}.");
    }

    private static object? Cell(ReportColumn column, string? raw, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return column.Kind switch
        {
            ReportColumnKind.Date => raw.Length > 10 ? raw[..10] : raw,
            ReportColumnKind.Number => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? Math.Round(n, 2) : raw,
            ReportColumnKind.Hours => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Round(m / 60, 2) : raw,
            ReportColumnKind.Bool => bool.TryParse(raw, out var b) ? b : raw,
            ReportColumnKind.PersonName => names?.GetValueOrDefault(raw.Trim()) ?? raw,
            _ => raw
        };
    }

    private static (DateTime Start, DateTime End) DateRange(ReportRequest request, string dataset)
    {
        if (Blank(request.StartDate) is not { } startText || Blank(request.EndDate) is not { } endText)
        {
            throw new ArgumentException($"Dataset '{dataset}' needs start_date and end_date (yyyy-MM-dd).");
        }

        var start = ParseDate(startText, "start_date");
        var end = ParseDate(endText, "end_date");
        if (end < start)
        {
            throw new ArgumentException("end_date is before start_date.");
        }

        if ((end - start).TotalDays + 1 > MaxDateSpanDays)
        {
            throw new ProjectorApiException(
                $"The date range is longer than {MaxDateSpanDays} days. Ask for a shorter range, one year at a time.",
                "date_window_exceeded");
        }

        return (start, end);
    }

    private static DateTime ParseDate(string text, string name) =>
        DateTime.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{name} '{text}' is not a date in the form yyyy-MM-dd.");

    private static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Time(DateTimeOffset? time) => time?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Uid(string? value, string name)
    {
        var uid = Blank(value);
        return uid is null || uid.All(char.IsAsciiDigit)
            ? uid
            : throw new ArgumentException($"{name} must be the number Projector shows (digits only).");
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    private async Task<T> WithRefreshAsync<T>(ProjectorConnection connection, Func<ProjectorConnection, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await action(connection);
        }
        catch (ProjectorApiException ex) when (IsAuthFailure(ex))
        {
            connection = await _connections.RefreshConnectionAsync(connection, ct);
            return await action(connection);
        }
    }

    private static bool IsAuthFailure(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
}
