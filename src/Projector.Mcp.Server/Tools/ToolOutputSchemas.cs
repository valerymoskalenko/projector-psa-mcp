using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Projector.Contracts.Holidays;
using Projector.Contracts.Resources;

namespace Projector.Mcp.Server.Tools;

/// <summary>
/// MCP outputSchema fragments for key agent tools so Copilot/Claude bind snake_case keys.
/// String constants are inlined into [Description] attributes; JSON Schema export is for hosts/tests.
/// </summary>
public static class ToolOutputSchemas
{
    public const string SearchCoverageRule =
        "searchCoverage.status is 'full' or 'partial'. When status is 'partial', do not present the result as complete; "
        + "say coverage is incomplete and follow searchCoverage.suggestion. count is returned rows; "
        + "when countIsLowerBound is true, total matching rows are unknown.";

    public const string HolidaysSchemaHint =
        "Output keys: start_date, end_date, calendars, count, active_resource_count, searchCoverage"
        + " (omit location unless filtered). " + SearchCoverageRule;

    public const string ResourcesListSchemaHint =
        "Output keys: resources, count, has_more, searchCoverage, resource_links. " + SearchCoverageRule;

    public const string ResourceGetSchemaHint =
        "Output keys: uri, resource, resource_links.";

    public const string TimecardsSchemaHint =
        "Output keys: resource_id, start_date, end_date, count, timecards[], searchCoverage. "
        + SearchCoverageRule;

    public const string TimeOffSchemaHint =
        "Output keys: resource_id, start_date, end_date, count, time_off[], searchCoverage. "
        + SearchCoverageRule;

    public const string UpcomingPtoSchemaHint =
        "Output keys: resource_id, start_date, end_date, count, pto[]"
        + " with minutes, hours, and source in holiday|schedule_timeoff|timecard, searchCoverage. "
        + SearchCoverageRule;

    public const string AvailabilitySchemaHint =
        "Output keys: start_date, end_date, required_minutes_per_week, people[], errors[], searchCoverage."
        + " availability.weeks always; availability.days only when show_availability_days is true."
        + " availability.bookings are Projector-native (dailyWeeklyFlag/scheduledMinutes), not exploded daily slices."
        + " Resource history is omitted. " + SearchCoverageRule;

    public const string EngagementsSchemaHint =
        "Output keys: engagements, count, has_more, searchCoverage, resource_links. " + SearchCoverageRule;

    public const string ProjectRolesSchemaHint =
        "Output keys: roles[], count, searchCoverage. " + SearchCoverageRule;

    public const string ProjectBookingsSchemaHint =
        "Output keys: bookings[], count, startDate, endDate, failed_project_codes, searchCoverage. "
        + SearchCoverageRule;

    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Export JSON Schema for a DTO (useful for hosts that consume structuredContent).</summary>
    public static JsonNode? ForType<T>() =>
        JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true
        });

    public static string HolidaysJsonSchema => ForType<ListHolidaysResponse>()?.ToJsonString() ?? "{}";

    public static string GetResourceJsonSchema => ForType<GetResourceResponse>()?.ToJsonString() ?? "{}";
}
