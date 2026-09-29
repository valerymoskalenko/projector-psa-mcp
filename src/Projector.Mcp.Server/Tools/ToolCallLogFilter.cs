using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// One log line per tool call — tool, client, outcome (ok / refusal code / exception), duration, server version and
/// the caller — so a user's problem can be found with one query. The SDK logs the client only once per session and
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

            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await next(context, cancellationToken);
                Log(logger, call, Outcome(result), Stopwatch.GetElapsedTime(started), exception: null);
                return result;
            }
            catch (Exception ex)
            {
                Log(logger, call, "exception:" + ex.GetType().Name, Stopwatch.GetElapsedTime(started), ex);
                throw;
            }
        };

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

    private static void Log(ILogger? logger, CallInfo call, string outcome, TimeSpan elapsed, Exception? exception)
    {
        if (logger is null)
        {
            return;
        }

        var level = outcome == "ok" ? LogLevel.Information
            : exception is null ? LogLevel.Warning
            : LogLevel.Error;
        logger.Log(
            level,
            exception,
            "Tool call {Tool} {Outcome} in {DurationMs} ms (client {Client}, server {ServerVersion}, requested as {RequestedTool})",
            call.Tool,
            outcome,
            (long)elapsed.TotalMilliseconds,
            call.Client,
            call.ServerVersion,
            call.RequestedTool ?? call.Tool);
    }
}
