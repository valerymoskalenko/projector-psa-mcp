using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Projector.Mcp.Server.Cli;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// M365 Copilot (Sydney) drops everything up to the first underscore before calling a tool:
/// it lists <c>get_resource</c> but calls <c>resource</c>. Before the SDK rejects an unknown name,
/// map it back to the single registered tool that ends with <c>_&lt;name&gt;</c>.
/// Legacy <c>projector_*</c> names are also accepted.
/// </summary>
internal static class CopilotToolNameFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            var tools = context.Server.ServerOptions.ToolCollection;
            if (context.Params is { } request
                && tools is not null
                && !tools.TryGetPrimitive(request.Name, out _)
                && Resolve(request.Name, tools.Select(t => t.ProtocolTool.Name)) is { } resolved
                && tools.TryGetPrimitive(resolved, out var tool))
            {
                request.Name = resolved;
                context.MatchedPrimitive = tool;
            }

            return next(context, cancellationToken);
        };

    /// <summary>Returns the registered tool name for <paramref name="requested"/>, or null if none or ambiguous.</summary>
    internal static string? Resolve(string requested, IEnumerable<string> registered)
    {
        var names = registered.ToList();
        if (string.IsNullOrWhiteSpace(requested)
            || names.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var canonical = ToolCatalog.Canonicalize(requested);
        if (names.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            return canonical;
        }

        var matches = names
            .Where(n => n.EndsWith("_" + requested, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
