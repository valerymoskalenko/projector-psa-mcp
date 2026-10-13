using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Marks file content parameters (<c>content_base64</c>, e.g. save_expenses cards[].receipt) in the tool list as
/// <c>"type": "string", "contentEncoding": "base64"</c>. Copilot Cowork shows such a parameter to its model as a
/// workspace file path and sends the file's content itself, so a receipt of any size reaches the server without the
/// model writing it out. The schema generated from the C# types has <c>["string", "null"]</c> and no
/// contentEncoding. Other clients see the same field as before. Only the listed copy changes: the registered tool,
/// which ToolArgumentFilter checks arguments against, keeps its schema.
/// </summary>
internal static class FileInputSchemaFilter
{
    public const string FileProperty = "content_base64";

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = result.Tools.Select(MarkFileInputs).ToList();
            return result;
        };

    internal static Tool MarkFileInputs(Tool tool)
    {
        if (!tool.InputSchema.GetRawText().Contains($"\"{FileProperty}\"", StringComparison.Ordinal))
        {
            return tool;
        }

        var node = JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions)!;
        Mark(node["inputSchema"]);
        return node.Deserialize<Tool>(McpJsonUtilities.DefaultOptions)!;
    }

    private static void Mark(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties && properties[FileProperty] is JsonObject file)
                {
                    file["type"] = "string";
                    file["contentEncoding"] = "base64";
                    file.Remove("default");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Mark(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Mark(child);
                }

                break;
        }
    }
}
