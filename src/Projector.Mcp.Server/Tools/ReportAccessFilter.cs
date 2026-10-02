using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.ApiClient;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Decides who sees get_report. The setting <c>Projector:GetReportUsers</c> lists Entra object ids (comma or
/// semicolon separated); <c>*</c> opens the tool to everyone; empty hides it from every hosted user. A caller
/// without a signed-in HTTP user (local stdio or the CLI) always has it. A hidden tool is left out of the tool
/// list and a call to it is answered like an unknown tool.
/// </summary>
internal static class ReportAccessFilter
{
    public const string ToolName = "get_report";

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            if (!IsAllowed(Setting(context.Services), context.User))
            {
                result.Tools = result.Tools.Where(t => t.Name != ToolName).ToList();
            }

            return result;
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (string.Equals(context.Params?.Name, ToolName, StringComparison.Ordinal)
                && !IsAllowed(Setting(context.Services), context.User))
            {
                var body = JsonSerializer.Serialize(new { error = "unknown_tool", message = $"Unknown tool: '{ToolName}'" });
                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = body }]
                });
            }

            return next(context, cancellationToken);
        };

    internal static bool IsAllowed(string? setting, ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var allowed = (setting ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Contains("*"))
        {
            return true;
        }

        var objectId = user.FindFirstValue("oid") ?? user.FindFirstValue(ToolCallLogFilter.ObjectIdClaimType);
        return objectId is not null && allowed.Contains(objectId, StringComparer.OrdinalIgnoreCase);
    }

    private static string? Setting(IServiceProvider? services) =>
        services?.GetService<IOptions<ProjectorOptions>>()?.Value.GetReportUsers;
}
