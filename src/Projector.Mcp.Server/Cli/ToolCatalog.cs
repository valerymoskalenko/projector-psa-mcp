using Microsoft.Extensions.DependencyInjection;
using Projector.Application.Resources;
using Projector.Application.Tools;
using Projector.Contracts.Resources;

namespace Projector.Mcp.Server.Cli;

/// <summary>
/// Maps CLI / alias names to application invocations for all agent tools.
/// </summary>
public static class ToolCatalog
{
    private const string LegacyPrefix = "projector_";

    /// <summary>
    /// CLI-only legacy names. Any canonical name with the old <c>projector_</c> prefix is also accepted.
    /// These are not MCP tools.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["projector_list_users"] = "list_resources",
        ["projector_get_user"] = "get_resource",
        ["projector_get_timecards"] = "list_timecards",
        ["projector_get_time_off"] = "list_time_off",
        ["projector_upcoming_pto"] = "list_upcoming_pto",
        ["projector_check_booking"] = "check_availability",
        ["get_resource_schedule"] = "get_schedule",
        ["projector_get_resource_schedule"] = "get_schedule",
        ["get_resource_overview"] = "get_overview",
        ["projector_get_resource_overview"] = "get_overview",
        ["list_project_bookings"] = "list_proj_bookings",
        ["projector_list_project_bookings"] = "list_proj_bookings"
    };

    public static readonly string[] CanonicalAgentTools =
    [
        "list_resources",
        "get_resource",
        "list_timecards",
        "get_schedule",
        "check_availability",
        "list_time_off",
        "list_engagements",
        "get_engagement",
        "list_upcoming_pto",
        "get_overview",
        "list_holidays",
        "list_project_roles",
        "list_proj_bookings",
        "list_time_projects",
        "get_timecard_options",
        "save_timecard",
        "get_report",
        "list_expenses",
        "save_expenses",
        "save_booking"
    ];

    public static string Canonicalize(string name)
    {
        if (Aliases.TryGetValue(name, out var canonical))
        {
            return canonical;
        }

        if (name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var unprefixed = name[LegacyPrefix.Length..];
            if (CanonicalAgentTools.Contains(unprefixed, StringComparer.OrdinalIgnoreCase))
            {
                return unprefixed.ToLowerInvariant();
            }
        }

        return name;
    }

    public static async Task<object> InvokeAsync(
        IServiceProvider services,
        string canonical,
        string connectionId,
        IReadOnlyDictionary<string, string> args,
        CancellationToken cancellationToken)
    {
        return canonical switch
        {
            "list_resources" => await ListResourcesAsync(services, connectionId, args, cancellationToken),
            "get_resource" => await GetResourceAsync(services, connectionId, args, cancellationToken),
            "list_timecards" => await ListTimecardsAsync(services, connectionId, args, cancellationToken),
            "list_time_off" => await ListTimeOffAsync(services, connectionId, args, cancellationToken),
            "get_schedule" => await GetScheduleAsync(services, connectionId, args, cancellationToken),
            "check_availability" => await CheckAvailabilityAsync(services, connectionId, args, cancellationToken),
            "list_engagements" => await ListEngagementsAsync(services, connectionId, args, cancellationToken),
            "get_engagement" => await GetEngagementAsync(services, connectionId, args, cancellationToken),
            "list_upcoming_pto" => await ListUpcomingPtoAsync(services, connectionId, args, cancellationToken),
            "get_overview" => await GetOverviewAsync(services, connectionId, args, cancellationToken),
            "list_holidays" => await ListHolidaysAsync(services, connectionId, args, cancellationToken),
            "list_project_roles" => await ListProjectRolesAsync(services, connectionId, args, cancellationToken),
            "list_proj_bookings" => await ListProjectBookingsAsync(services, connectionId, args, cancellationToken),
            "list_time_projects" => await ListTimeProjectsAsync(services, connectionId, args, cancellationToken),
            "get_timecard_options" => await GetTimecardOptionsAsync(services, connectionId, args, cancellationToken),
            "save_timecard" => await SaveTimecardAsync(services, connectionId, args, cancellationToken),
            "get_report" => await GetReportAsync(services, connectionId, args, cancellationToken),
            "list_expenses" => await ListExpensesAsync(services, connectionId, args, cancellationToken),
            "save_expenses" => await SaveExpensesAsync(services, connectionId, args, cancellationToken),
            "save_booking" => await SaveBookingAsync(services, connectionId, args, cancellationToken),
            _ => throw new ArgumentException(
                $"Unknown tool '{canonical}'. Known: {string.Join(", ", CanonicalAgentTools)}")
        };
    }

    private static async Task<object> ListResourcesAsync(
        IServiceProvider services,
        string connectionId,
        IReadOnlyDictionary<string, string> args,
        CancellationToken cancellationToken)
    {
        var svc = services.GetRequiredService<ResourceService>();
        args.TryGetValue("query", out var query);
        var includeInactive = GetBool(args, "include_inactive");
        var maxRows = GetInt(args, "max_rows", 50);
        return await svc.ListAsync(
            connectionId,
            new ListResourcesRequest(query, includeInactive, maxRows),
            cancellationToken);
    }

    private static async Task<object> GetResourceAsync(
        IServiceProvider services,
        string connectionId,
        IReadOnlyDictionary<string, string> args,
        CancellationToken cancellationToken)
    {
        var svc = services.GetRequiredService<ResourceService>();
        var id = GetOneOf(args, "resource_id", "full_name", "email", "id")
            ?? throw new ArgumentException(Tools.ResourceTools.NoPersonMessage);
        var includeHistory = GetBool(args, "include_history");
        var includeUdfs = !args.ContainsKey("include_udfs") || GetBool(args, "include_udfs");
        return await svc.GetAsync(
            connectionId,
            new GetResourceRequest(id, includeHistory, includeUdfs),
            cancellationToken);
    }

    private static Task<object> ListTimecardsAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("resource_id", out var resourceId);
        var start = Require(args, "start_date");
        var end = Require(args, "end_date");
        args.TryGetValue("status", out var status);
        args.TryGetValue("project_code", out var projectCode);
        args.TryGetValue("query", out var query);
        args.TryGetValue("group_by", out var groupBy);
        return tools.ListTimecardsAsync(connectionId, resourceId, start, end, status, projectCode, ct, query,
            GetBool(args, "compact"), groupBy);
    }

    private static Task<object> ListTimeOffAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("resource_id", out var resourceId);
        return tools.ListTimeOffAsync(connectionId, resourceId, Require(args, "start_date"), Require(args, "end_date"), ct);
    }

    private static Task<object> GetScheduleAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("resource_id", out var resourceId);
        return tools.GetResourceScheduleAsync(connectionId, resourceId, Require(args, "start_date"), Require(args, "end_date"), ct);
    }

    private static Task<object> CheckAvailabilityAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        var people = new List<string>();
        if (args.TryGetValue("people", out var peopleRaw) && !string.IsNullOrWhiteSpace(peopleRaw))
        {
            people.AddRange(peopleRaw
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (args.TryGetValue("resource_id", out var resourceId) && !string.IsNullOrWhiteSpace(resourceId))
        {
            people.Add(resourceId.Trim());
        }

        // No people = the signed-in user (the service fills it in).
        people = people.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        double? hours = GetDouble(args, "required_hours_per_week");
        double? minutes = GetDouble(args, "required_minutes_per_week");
        var showDays = GetBool(args, "show_availability_days");
        return tools.CheckAvailabilityAsync(
            connectionId, people, Require(args, "start_date"), Require(args, "end_date"),
            hours, minutes, showDays, ct);
    }

    private static Task<object> ListEngagementsAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("query", out var query);
        args.TryGetValue("manager_query", out var managerQuery);
        args.TryGetValue("manager_role", out var managerRole);
        bool? includeClosed = args.ContainsKey("include_closed") ? GetBool(args, "include_closed") : null;
        var maxRows = GetInt(args, "max_rows", 50);
        return tools.ListEngagementsAsync(connectionId, query, managerQuery, managerRole, includeClosed, maxRows, ct);
    }

    private static Task<object> GetEngagementAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        return tools.GetEngagementAsync(connectionId, Require(args, "code"), ct);
    }

    private static Task<object> ListUpcomingPtoAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("start_date", out var start);
        args.TryGetValue("end_date", out var end);
        args.TryGetValue("resource_id", out var resourceId);
        return tools.ListUpcomingPtoAsync(connectionId, resourceId, start, end, ct);
    }

    private static Task<object> GetOverviewAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        return tools.GetResourceOverviewAsync(
            connectionId, Require(args, "resource_id"), Require(args, "start_date"), Require(args, "end_date"), ct);
    }

    private static Task<object> ListHolidaysAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        args.TryGetValue("end_date", out var end);
        args.TryGetValue("location", out var location);
        return tools.ListHolidaysAsync(connectionId, Require(args, "start_date"), end, location, ct);
    }

    private static Task<object> ListProjectRolesAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        return tools.ListProjectRolesAsync(connectionId, ParseProjectCodes(args), ct, GetBool(args, "include_task_plan"));
    }

    private static Task<object> ListProjectBookingsAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var tools = services.GetRequiredService<ProjectorToolService>();
        return tools.ListProjectBookingsAsync(
            connectionId,
            ParseProjectCodes(args),
            Require(args, "start_date"),
            Require(args, "end_date"),
            ct);
    }

    private static Task<object> ListTimeProjectsAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var svc = services.GetRequiredService<TimeEntryToolService>();
        args.TryGetValue("query", out var query);
        return svc.ListTimeProjectsAsync(
            connectionId, Require(args, "work_date"), query, GetInt(args, "max_rows", 50), ct, GetInt(args, "offset", 0),
            chargeableOnly: !args.ContainsKey("chargeable_only") || GetBool(args, "chargeable_only"));
    }

    private static Task<object> GetTimecardOptionsAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var svc = services.GetRequiredService<TimeEntryToolService>();
        args.TryGetValue("query", out var query);
        return svc.GetTimecardOptionsAsync(
            connectionId, Require(args, "project_code"), Require(args, "work_date"), ct, query,
            GetInt(args, "max_tasks", TimeEntryToolService.DefaultMaxTasks), GetInt(args, "offset", 0));
    }

    private static Task<object> SaveTimecardAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var svc = services.GetRequiredService<TimeEntryToolService>();
        var dryRun = GetBool(args, "dry_run", defaultValue: true);

        // --cards-json <file>: the same cards array the MCP tool takes. Otherwise the single-card flags make one card.
        if (args.TryGetValue("cards_json", out var cardsFile) && !string.IsNullOrWhiteSpace(cardsFile))
        {
            var cards = System.Text.Json.JsonSerializer.Deserialize<Tools.SaveTimecardCard[]>(File.ReadAllText(cardsFile))
                ?? throw new ArgumentException("--cards-json must contain a JSON array of cards.");
            return svc.SaveTimecardsAsync(connectionId, cards.Select(c => c.ToInput()).ToList(), dryRun, ct);
        }

        var hours = double.TryParse(Require(args, "hours"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var h)
            ? h
            : throw new ArgumentException("--hours must be a number, e.g. 1.5");
        args.TryGetValue("timecard_uid", out var timecardUid);
        args.TryGetValue("location", out var location);
        args.TryGetValue("udf1", out var udf1);
        args.TryGetValue("udf2", out var udf2);
        return svc.SaveTimecardsAsync(
            connectionId,
            [
                new SaveTimecardInput(
                    Require(args, "work_date"),
                    hours,
                    Require(args, "project_code"),
                    Require(args, "task"),
                    Require(args, "role"),
                    Require(args, "narrative"),
                    timecardUid,
                    location,
                    udf1,
                    udf2)
            ],
            dryRun,
            ct);
    }

    private static Task<object> ListExpensesAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string? Text(string key) => args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        return services.GetRequiredService<ExpenseToolService>().ListExpensesAsync(
            connectionId,
            Text("resource_id"),
            Text("report"),
            GetInt(args, "months", ExpenseToolService.DefaultMonths),
            GetBool(args, "unreceived_only"),
            Text("query"),
            GetBool(args, "include_options"),
            Text("options_date"),
            Text("project_code"),
            GetInt(args, "max_rows", ExpenseToolService.DefaultMaxProjects),
            GetInt(args, "offset", 0),
            ct);
    }

    /// <summary>
    /// --cards-json &lt;file&gt;: the cards array the MCP tool takes. For local tests a receipt may give
    /// <c>file_path</c> instead of <c>content_base64</c>: the CLI reads the file and sends it as base64.
    /// </summary>
    private static Task<object> SaveExpensesAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var file = Require(args, "cards_json");
        var cards = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) as System.Text.Json.Nodes.JsonArray
            ?? throw new ArgumentException("--cards-json must contain a JSON array of cards.");
        foreach (var receipt in cards.Select(c => c?["receipt"]).OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (receipt["file_path"]?.GetValue<string>() is { Length: > 0 } path)
            {
                var full = Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(file))!);
                receipt["content_base64"] = Convert.ToBase64String(File.ReadAllBytes(full));
                receipt["file_name"] ??= Path.GetFileName(full);
                receipt.Remove("file_path");
            }
        }

        var parsed = System.Text.Json.JsonSerializer.Deserialize<Tools.SaveExpenseCard[]>(cards)
            ?? throw new ArgumentException("--cards-json must contain a JSON array of cards.");
        args.TryGetValue("report", out var report);
        args.TryGetValue("report_name", out var reportName);
        return services.GetRequiredService<ExpenseToolService>().SaveExpensesAsync(
            connectionId,
            string.IsNullOrWhiteSpace(report) ? null : report,
            string.IsNullOrWhiteSpace(reportName) ? null : reportName,
            parsed.Select(c => c.ToInput()).ToList(),
            GetBool(args, "dry_run", defaultValue: true),
            ct,
            GetBool(args, "brief"));
    }

    private static Task<object> SaveBookingAsync(
        IServiceProvider services, string connectionId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string? Text(string key) => args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        // Without --hours the current amounts stay (extra days and comments only).
        var hours = GetDouble(args, "hours");

        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        IReadOnlyList<BookingExtraDayInput>? extras = null;
        if (Text("extra_days_json") is { } extrasFile)
        {
            extras = System.Text.Json.JsonSerializer.Deserialize<List<BookingExtraDayInput>>(File.ReadAllText(extrasFile), jsonOpts)
                ?? throw new ArgumentException("--extra-days-json must contain a JSON array.");
        }

        IReadOnlyList<BookingCommentInput>? comments = null;
        if (Text("comments_json") is { } commentsFile)
        {
            comments = System.Text.Json.JsonSerializer.Deserialize<List<BookingCommentInput>>(File.ReadAllText(commentsFile), jsonOpts)
                ?? throw new ArgumentException("--comments-json must contain a JSON array.");
        }

        return services.GetRequiredService<BookingToolService>().SaveBookingAsync(
            connectionId,
            Require(args, "resource"),
            Require(args, "project_code"),
            Require(args, "start_date"),
            Require(args, "end_date"),
            hours,
            Text("scheduling_mode") ?? "weekly",
            Text("task"),
            Text("role_name"),
            extras,
            comments,
            GetBool(args, "dry_run", defaultValue: true),
            ct);
    }

    private static Task<object> GetReportAsync(
        IServiceProvider services,
        string connectionId,
        IReadOnlyDictionary<string, string> args,
        CancellationToken ct)
    {
        string? Text(string key) => args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        return services.GetRequiredService<ReportToolService>().GetReportAsync(
            connectionId,
            new ReportRequest
            {
                Dataset = Text("dataset"),
                Code = Text("code"),
                SpecUid = Text("spec_uid"),
                OutputUid = Text("output_uid"),
                StartDate = Text("start_date"),
                EndDate = Text("end_date"),
                CutoffDate = Text("cutoff_date"),
                Bucket = Text("bucket"),
                CostCenter = Text("cost_center"),
                By = Text("by"),
                BillableOnly = GetBool(args, "billable_only"),
                // On unless switched off: --include-unapproved false
                IncludeUnapproved = !args.TryGetValue("include_unapproved", out var unapproved)
                    || !string.Equals(unapproved, "false", StringComparison.OrdinalIgnoreCase),
                IncludeTimeOff = GetBool(args, "include_time_off"),
                IncludeClosed = GetBool(args, "include_closed"),
                Query = Text("query"),
                Columns = Text("columns")?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                MaxRows = GetInt(args, "max_rows", ReportToolService.DefaultMaxRows),
                Cursor = Text("cursor")
            },
            ct);
    }

    private static IReadOnlyList<string> ParseProjectCodes(IReadOnlyDictionary<string, string> args)
    {
        var codes = new List<string>();
        if (args.TryGetValue("project_code", out var single) && !string.IsNullOrWhiteSpace(single))
        {
            codes.AddRange(single.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (args.TryGetValue("project_codes", out var many) && !string.IsNullOrWhiteSpace(many))
        {
            codes.AddRange(many.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        codes = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0)
        {
            throw new ArgumentException("Provide --project-code or --project-codes.");
        }

        return codes;
    }

    private static string Require(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument --{key.Replace('_', '-')}");

    private static string? GetOneOf(IReadOnlyDictionary<string, string> args, params string[] keys)
    {
        string? found = null;
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                if (found is not null)
                {
                    throw new ArgumentException($"Provide only one of: {string.Join(", ", keys)}.");
                }

                found = value;
            }
        }

        return found;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var v)
        && (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1");

    /// <summary>A switch with a default: the write commands check only unless --dry-run false is given.</summary>
    private static bool GetBool(IReadOnlyDictionary<string, string> args, string key, bool defaultValue) =>
        args.ContainsKey(key) ? GetBool(args, key) : defaultValue;

    private static int GetInt(IReadOnlyDictionary<string, string> args, string key, int defaultValue) =>
        args.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : defaultValue;

    private static double? GetDouble(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var v) && double.TryParse(v, out var n) ? n : null;
}
