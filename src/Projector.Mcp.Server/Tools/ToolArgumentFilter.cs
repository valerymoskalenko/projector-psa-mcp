using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// Makes tool arguments bind the way agents actually send them. Clients send <c>null</c> for optional parameters they
/// don't use and whole numbers as <c>20.0</c>; the SDK can't bind either to an <c>int</c> and answers with a bare
/// "An error occurred invoking '&lt;tool&gt;'" (seen 2026-09-28: list_time_projects and get_timecard_options failed a
/// whole session). Here a null argument is dropped (the parameter's default applies), a whole number becomes an
/// integer, and anything that still can't bind gets a structured <c>invalid_argument</c> error naming the parameter.
/// </summary>
internal static class ToolArgumentFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            var tools = context.Server.ServerOptions.ToolCollection;
            if (context.Params is { Arguments: { } arguments } request
                && tools is not null
                && tools.TryGetPrimitive(request.Name, out var tool))
            {
                var problem = Normalize(arguments, tool.ProtocolTool.InputSchema);
                if (problem is not null)
                {
                    context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolArgumentFilter))
                        .LogWarning("Tool call refused: invalid_argument {Tool} {ErrorMessage}", request.Name, problem);
                    return ValueTask.FromResult(Error(problem));
                }
            }

            return next(context, cancellationToken);
        };

    /// <summary>Fixes the arguments in place; returns a message when one can't be used.</summary>
    internal static string? Normalize(IDictionary<string, JsonElement> arguments, JsonElement schema)
    {
        var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var p) ? p : default;
        foreach (var name in arguments.Keys.ToList())
        {
            var value = arguments[name];
            if (value.ValueKind == JsonValueKind.Null)
            {
                arguments.Remove(name);
                continue;
            }

            var type = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var prop)
                ? SchemaType(prop)
                : null;
            switch (type)
            {
                case "integer":
                    if (TryWholeNumber(value, out var whole))
                    {
                        arguments[name] = JsonSerializer.SerializeToElement(whole);
                    }
                    else
                    {
                        return $"{name} must be a whole number (got {value.GetRawText()}).";
                    }

                    break;
                case "number":
                    if (TryNumber(value, out var number))
                    {
                        arguments[name] = JsonSerializer.SerializeToElement(number);
                    }
                    else
                    {
                        return $"{name} must be a number (got {value.GetRawText()}).";
                    }

                    break;
                case "boolean":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        break;
                    }

                    if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag))
                    {
                        arguments[name] = JsonSerializer.SerializeToElement(flag);
                        break;
                    }

                    return $"{name} must be true or false (got {value.GetRawText()}).";
            }
        }

        return null;
    }

    /// <summary>The JSON schema type, ignoring "null" in a type list (nullable parameters).</summary>
    private static string? SchemaType(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return null;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => type.EnumerateArray()
                .Select(t => t.GetString())
                .FirstOrDefault(t => t is not null && t != "null"),
            _ => null
        };
    }

    private static bool TryWholeNumber(JsonElement value, out int result)
    {
        result = 0;
        if (!TryNumber(value, out var number) || number != Math.Floor(number) || number is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        result = (int)number;
        return true;
    }

    private static bool TryNumber(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            _ => false
        } && double.IsFinite(result);
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = "invalid_argument", message }, JsonOptions) }]
    };
}
