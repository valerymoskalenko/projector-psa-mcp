using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Projector.Mcp.Server.Resources;

[McpServerResourceType]
public sealed class ProjectorReferenceResources
{
    private static readonly Dictionary<string, string> CatalogFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["locations"] = "locations.json",
        ["departments-titles"] = "departments-titles.json",
        ["cost-centers"] = "cost-centers.json",
        ["resource-types"] = "resource-types.json",
        ["udf-departments"] = "udf-departments.json",
        ["udf-divisions"] = "udf-divisions.json",
        ["udf-team-managers"] = "udf-team-managers.json",
        ["udf-technologies"] = "udf-technologies.json"
    };

    [McpServerResource(UriTemplate = "projector://reference/{catalog}", Name = "projector_reference_catalog", MimeType = "application/json")]
    [Description("Static Projector reference catalog (locations, departments-titles, cost-centers, resource-types, udf-*).")]
    public static TextResourceContents GetReferenceCatalog(string catalog)
    {
        if (!CatalogFiles.TryGetValue(catalog, out var fileName))
        {
            throw new McpException($"Unknown reference catalog: {catalog}");
        }

        var path = ResolveCatalogPath(fileName);
        if (!File.Exists(path))
        {
            throw new McpException($"Catalog file not found: {fileName}");
        }

        var json = File.ReadAllText(path);
        return new TextResourceContents
        {
            Uri = $"projector://reference/{catalog}",
            MimeType = "application/json",
            Text = json
        };
    }

    private static string ResolveCatalogPath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "resources", "catalogs", fileName),
            Path.Combine(baseDir, "..", "..", "..", "resources", "catalogs", fileName),
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "src",
                "Projector.Mcp.Server",
                "resources",
                "catalogs",
                fileName)
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? Path.Combine(baseDir, "resources", "catalogs", fileName);
    }
}
