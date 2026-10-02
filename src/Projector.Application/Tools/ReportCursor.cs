using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Projector.Application.Tools;

/// <summary>What the agent asked get_report for. A cursor carries it, so the next call needs no other argument.</summary>
public sealed record ReportRequest
{
    [JsonPropertyName("d")] public string? Dataset { get; init; }
    [JsonPropertyName("c")] public string? Code { get; init; }
    [JsonPropertyName("s")] public string? SpecUid { get; init; }
    [JsonPropertyName("o")] public string? OutputUid { get; init; }
    [JsonPropertyName("sd")] public string? StartDate { get; init; }
    [JsonPropertyName("ed")] public string? EndDate { get; init; }
    [JsonPropertyName("cd")] public string? CutoffDate { get; init; }
    [JsonPropertyName("b")] public string? Bucket { get; init; }
    [JsonPropertyName("cc")] public string? CostCenter { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
    [JsonPropertyName("bo")] public bool BillableOnly { get; init; }
    [JsonPropertyName("iu")] public bool IncludeUnapproved { get; init; } = true;
    [JsonPropertyName("it")] public bool IncludeTimeOff { get; init; }
    [JsonPropertyName("ic")] public bool IncludeClosed { get; init; }
    [JsonPropertyName("q")] public string? Query { get; init; }
    [JsonPropertyName("col")] public string[]? Columns { get; init; }
    [JsonPropertyName("m")] public int MaxRows { get; init; } = ReportToolService.DefaultMaxRows;

    /// <summary>The <c>next_cursor</c> of a previous answer; never part of a cursor itself.</summary>
    [JsonIgnore] public string? Cursor { get; init; }
}

/// <summary>Where the next page starts.</summary>
public sealed record ReportPosition
{
    /// <summary>Rows already returned from a cached table (report, ginsu).</summary>
    [JsonPropertyName("o")] public int Offset { get; init; }

    /// <summary>Projector's own "continue after" values (projects: project code; time_cards: approval time and card id).</summary>
    [JsonPropertyName("a")] public string? After { get; init; }
    [JsonPropertyName("a2")] public string? After2 { get; init; }

    /// <summary>A report run or export batch that was started and may still be running.</summary>
    [JsonPropertyName("r")] public string? RunId { get; init; }

    /// <summary>Total rows, counted on the first page.</summary>
    [JsonPropertyName("t")] public int? Total { get; init; }
}

/// <summary>
/// The opaque <c>next_cursor</c> of get_report: the request and the position, as base64url JSON. It holds nothing
/// secret and no data. It is bound to the user it was given to; another user's cursor is refused.
/// </summary>
public static class ReportCursor
{
    private const int Version = 1;

    // Nulls are left out; false is written, because IncludeUnapproved defaults to true.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("u")] string User,
        [property: JsonPropertyName("r")] ReportRequest Request,
        [property: JsonPropertyName("p")] ReportPosition Position);

    public static string Encode(string userKey, ReportRequest request, ReportPosition position)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(Version, UserHash(userKey), request, position), JsonOptions);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="ArgumentException">The text is not a cursor of this server version, or belongs to another user.</exception>
    public static (ReportRequest Request, ReportPosition Position) Decode(string cursor, string userKey)
    {
        Payload? payload;
        try
        {
            var text = cursor.Trim().Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(text), JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            payload = null;
        }

        if (payload?.Request is null || payload.Version != Version || payload.User != UserHash(userKey))
        {
            throw new ArgumentException(
                "cursor is not valid here: pass the next_cursor of your own previous get_report answer unchanged, " +
                "or start again without cursor.");
        }

        return (payload.Request, payload.Position ?? new ReportPosition());
    }

    private static string UserHash(string userKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userKey)), 0, 6);
}
