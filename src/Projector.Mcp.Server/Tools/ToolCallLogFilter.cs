using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.ApiClient.Xml;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// One log line per tool call — tool, client, outcome (ok / refusal code / exception), duration, server version and
/// the caller — so a user's problem can be found with one query. The line also carries the answer's size
/// and rows, whether it was cut or has another page, the Projector calls behind it (count, time, size, rows; each
/// call has its own line, see <see cref="ProjectorCallStatsHandler"/>) and the shape of the arguments (names, paging
/// values, date range in days), so the logs show when a tool reaches its limits. The SDK logs the client only once per session and
/// only "IsError = True" for a failure. The same fields are a log scope for everything logged during the call
/// (refusals, save audit, Projector warnings), and tags on the request telemetry (user_Id / session_Id in App Insights).
/// Only pseudonymous ids are logged: the Entra object id and the connection id, never names or e-mail addresses.
/// Registered first, so it wraps the other call-tool filters and times the whole call.
/// </summary>
internal static class ToolCallLogFilter
{
    public const string LoggerCategory = "Projector.Mcp.ToolCall";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var call = Describe(context);
            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
            Tag(Activity.Current, call);
            using var scope = logger?.BeginScope(call.ScopeState());

            // Read before the other filters change the arguments; the Projector calls of this tool call add themselves.
            var arguments = DescribeArguments(context.Params?.Arguments);
            var projector = ProjectorCallStats.Begin();
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await next(context, cancellationToken);
                Log(logger, call, Outcome(result), Stopwatch.GetElapsedTime(started), exception: null,
                    arguments, DescribeOutput(result), projector.Calls);
                return result;
            }
            catch (Exception ex)
            {
                Log(logger, call, "exception:" + ex.GetType().Name, Stopwatch.GetElapsedTime(started), ex,
                    arguments, output: null, projector.Calls);
                throw;
            }
        };

    /// <summary>
    /// The shape of a call's arguments, never their contents: which arguments were given, the values of the paging
    /// and switch arguments in <see cref="LoggedArgumentValues"/>, the length of list arguments, and the length of
    /// the dates asked for (start_date, end_date, work_date as yyyy-MM-dd) with the length of the range in days.
    /// </summary>
    internal sealed record ArgumentInfo(
        string Names,
        string Values,
        int? DateSpanDays,
        string? StartDate = null,
        string? EndDate = null,
        string? WorkDate = null);

    /// <summary>Arguments whose value says how much was asked for and nothing about a person, project or search.</summary>
    private static readonly HashSet<string> LoggedArgumentValues = new(StringComparer.Ordinal)
    {
        "max_rows", "max_tasks", "offset", "compact", "include_closed", "include_inactive", "chargeable_only",
        "dry_run", "include_history", "include_udfs", "include_task_plan", "show_availability_days", "status",
        "manager_role", "dataset", "bucket", "by", "billable_only", "include_unapproved", "include_time_off"
    };

    internal static ArgumentInfo DescribeArguments(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var given = (arguments ?? [])
            .Where(a => a.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .ToList();
        var values = new List<string>();
        foreach (var (name, value) in given)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                values.Add($"{name}=[{value.GetArrayLength()}]");
            }
            else if (LoggedArgumentValues.Contains(name))
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                values.Add($"{name}={(text is { Length: > 20 } ? text[..20] : text)}");
            }
        }

        DateTime? Date(string name) =>
            given.FirstOrDefault(a => a.Key == name).Value is { ValueKind: JsonValueKind.String } v
            && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.Date
                : null;

        static string? Text(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var startDate = Date("start_date");
        var endDate = Date("end_date");
        var days = startDate is { } start && endDate is { } end ? (int)(end - start).TotalDays + 1 : (int?)null;
        return new ArgumentInfo(
            string.Join(",", given.Select(a => a.Key)), string.Join(";", values), days,
            Text(startDate), Text(endDate), Text(Date("work_date")));
    }

    /// <summary>
    /// The size of an answer: bytes of its text (what the model reads; the same data goes out once more as structured
    /// content), rows, and whether there is more.
    /// </summary>
    /// <param name="Rows">count / tasks_count of the answer, else the length of its longest list.</param>
    /// <param name="Total">total / tasks_total: rows that matched before paging.</param>
    /// <param name="HasMore">has_more / tasks_has_more: another page can be asked for.</param>
    /// <param name="Partial">searchCoverage says Projector cut the list.</param>
    internal sealed record OutputInfo(long Bytes, int? Rows, int? Total, bool? HasMore, bool? Partial);

    internal static OutputInfo DescribeOutput(CallToolResult result)
    {
        var texts = result.Content.OfType<TextContentBlock>().Select(t => t.Text ?? string.Empty).ToList();
        var bytes = texts.Sum(t => (long)Encoding.UTF8.GetByteCount(t));
        if (result.IsError == true || texts.Count == 0)
        {
            return new OutputInfo(bytes, null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(texts[0]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new OutputInfo(bytes, null, null, null, null);
            }

            int? Number(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            bool? Flag(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

            var longestList = root.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                .Select(p => (int?)p.Value.GetArrayLength())
                .Max();
            var partial = root.TryGetProperty("searchCoverage", out var coverage) && coverage.ValueKind == JsonValueKind.Object
                && coverage.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                    ? status.GetString() == "partial"
                    : (bool?)null;
            return new OutputInfo(
                bytes,
                Number("count") ?? Number("tasks_count") ?? longestList,
                Number("total") ?? Number("tasks_total"),
                Flag("has_more") ?? Flag("tasks_has_more"),
                partial);
        }
        catch (JsonException)
        {
            return new OutputInfo(bytes, null, null, null, null);
        }
    }

    internal sealed record CallInfo(
        string Tool,
        string? RequestedTool,
        string Client,
        string? ServerVersion,
        string? UserId,
        string? ConnectionId,
        string? SessionId,
        string? HeaderNames)
    {
        public Dictionary<string, object?> ScopeState() => new()
        {
            ["Tool"] = Tool,
            ["Client"] = Client,
            ["ServerVersion"] = ServerVersion,
            ["UserId"] = UserId,
            ["ConnectionId"] = ConnectionId,
            ["SessionId"] = SessionId,
            ["HeaderNames"] = HeaderNames
        };
    }

    private static CallInfo Describe(RequestContext<CallToolRequestParams> context)
    {
        var requested = context.Params?.Name ?? string.Empty;
        var registered = context.Server.ServerOptions.ToolCollection?.Select(t => t.ProtocolTool.Name) ?? [];
        var tool = CopilotToolNameFilter.Resolve(requested, registered) ?? requested;
        var client = context.JsonRpcRequest.Context?.ClientInfo ?? context.Server.ClientInfo;
        var user = context.User;
        var http = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;

        return new CallInfo(
            Tool: tool,
            RequestedTool: string.Equals(tool, requested, StringComparison.Ordinal) ? null : requested,
            Client: ClientName(client?.Name, client?.Version, http?.Request.Headers.UserAgent.FirstOrDefault()),
            ServerVersion: context.Server.ServerOptions.ServerInfo?.Version,
            UserId: user?.FindFirstValue("oid") ?? user?.FindFirstValue(ObjectIdClaimType),
            ConnectionId: user?.FindFirstValue("connection_id"),
            SessionId: SessionOrConversationId(http?.Request.Headers),
            HeaderNames: http is null ? null : HeaderNames(http.Request.Headers));
    }

    /// <summary>JwtBearer maps the token's <c>oid</c> claim to this type (seen in production 2026-09-29).</summary>
    internal const string ObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>
    /// The MCP client name, or the User-Agent when the client didn't send one with this request: Cowork names itself
    /// only on initialize, and each stateless call is a new request.
    /// </summary>
    internal static string ClientName(string? name, string? version, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"{name} {version}".Trim();
        }

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "unknown";
        }

        var ua = userAgent.Trim();
        return "ua:" + (ua.Length <= 80 ? ua : ua[..80]);
    }

    /// <summary>
    /// The MCP session id, or Copilot's conversation id: stateless Copilot/Cowork calls have no MCP session, but carry
    /// <c>X-Microsoft-AI-ConversationId</c>, which groups the calls of one conversation.
    /// </summary>
    internal static string? SessionOrConversationId(IHeaderDictionary? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var id = headers["Mcp-Session-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(id) ? headers["X-Microsoft-AI-ConversationId"].FirstOrDefault() : id;
    }

    /// <summary>
    /// Request header names only (never values), to find a conversation id a client might send
    /// (M365 Copilot's MCP calls are stateless: no Mcp-Session-Id).
    /// </summary>
    internal static string HeaderNames(IHeaderDictionary headers) =>
        string.Join(",", headers.Keys.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>"ok", or the error code from a refused call's <c>{"error": "..."}</c> body ("error" when there is none).</summary>
    internal static string Outcome(CallToolResult result)
    {
        if (result.IsError != true)
        {
            return "ok";
        }

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var code)
                    && code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString() ?? "error";
                }
            }
            catch (JsonException)
            {
                // Not our error shape (e.g. the SDK's own "An error occurred invoking ..." text).
            }
        }

        return "error";
    }

    private static void Tag(Activity? activity, CallInfo call)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("mcp.tool", call.Tool);
        activity.SetTag("mcp.client", call.Client);
        if (call.UserId is not null)
        {
            // Azure Monitor maps these to user_AuthenticatedId / user_Id.
            activity.SetTag("enduser.id", call.UserId);
            activity.SetTag("enduser.pseudo.id", call.UserId);
        }

        if (call.SessionId is not null)
        {
            activity.SetTag("session.id", call.SessionId);
        }
    }

    internal static void Log(
        ILogger? logger,
        CallInfo call,
        string outcome,
        TimeSpan elapsed,
        Exception? exception,
        ArgumentInfo arguments,
        OutputInfo? output,
        IReadOnlyList<ProjectorCallStat> projector)
    {
        if (logger is null)
        {
            return;
        }

        var level = outcome == "ok" ? LogLevel.Information
            : exception is null ? LogLevel.Warning
            : LogLevel.Error;
        // ProjectorMs adds the calls up, so parallel calls can make it longer than the tool call itself.
        logger.Log(
            level,
            exception,
            "Tool call {Tool} {Outcome} in {DurationMs} ms, {OutputKb} KB, {OutputRows} rows " +
            "(client {Client}, server {ServerVersion}, requested as {RequestedTool}); " +
            "Projector {ProjectorCalls} call(s), {ProjectorMs} ms, {ProjectorKb} KB, {ProjectorRows} rows; " +
            "total {OutputTotal}, more {HasMore}, partial {Partial}; args [{ArgNames}] {ArgValues}, " +
            "dates {StartDate}..{EndDate} ({DateSpanDays} day(s)), work date {WorkDate}",
            call.Tool,
            outcome,
            (long)elapsed.TotalMilliseconds,
            ProjectorCallStats.Kb(output?.Bytes),
            output?.Rows,
            call.Client,
            call.ServerVersion,
            call.RequestedTool ?? call.Tool,
            projector.Count,
            projector.Sum(p => p.DurationMs),
            ProjectorCallStats.Kb(projector.Sum(p => p.ResponseBytes ?? 0)),
            projector.Sum(p => p.Rows ?? 0),
            output?.Total,
            output?.HasMore,
            output?.Partial,
            arguments.Names,
            arguments.Values,
            arguments.StartDate,
            arguments.EndDate,
            arguments.DateSpanDays,
            arguments.WorkDate);
    }
}
