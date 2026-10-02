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
        "Output keys: resource_id (\"me\" for the signed-in user), start_date, end_date, count, by_date[] (date, hours, card_count, "
        + "hours_by_status: posted hours per day, so compare days with get_schedule without adding cards up), timecards[], searchCoverage. "
        + "Each card has taskName, taskPath (parent tasks > task), taskWbsCode, timecardUid, projectTaskUid, projectRoleUid "
        + "and projectRateTypeUid (use them with save_timecard). Every status is listed, Rejected included (with the "
        + "rejection reason when Projector gives one). The signed-in user's own cards have editable: true = Draft or "
        + "Rejected (save_timecard with timecard_uid can fix it), false = fix it in Projector. "
        + "compact=true returns short cards (workDate, workHours, projectCode, projectName, taskPath, taskWbsCode, roleName, "
        + "rateTypeName, status, description, rejectedReason, timecardUid, editable), about half the size; use it for history. "
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
        "Output keys: engagements, count, has_more, note (set when Projector did not return the details in time: " +
        "managers and projects may then be missing), searchCoverage, resource_links. " + SearchCoverageRule;

    public const string ProjectRolesSchemaHint =
        "Output keys: roles[], count, taskPlan (only with include_task_plan: projectCode, planStartDate, planEndDate, "
        + "hoursPerDay, taskCount, totalEffortHours, tasks[] with wbsCode, taskName, taskPath, summaryTask, taskType, "
        + "plannedStartDate, plannedEndDate, earliestStartDate, durationDays, effortHours, openForTime, completed, "
        + "predecessors, roles[] with roleName, displayName, effortHours), searchCoverage. " + SearchCoverageRule;

    public const string ProjectBookingsSchemaHint =
        "Output keys: bookings[] (each with notes[] when the week has booking notes), count, startDate, endDate, "
        + "failed_project_codes, searchCoverage. "
        + SearchCoverageRule;

    public const string ReportSchemaHint =
        "Output keys: dataset, source, status (ok, running or empty), data_as_of, count, total, has_more, next_cursor, "
        + "columns[], rows[] (each row is a list of values in the order of columns), available_columns[] (first part "
        + "only), recent_report_runs[] (name, status, completed), note. Without a dataset: datasets[] and "
        + "recent_report_runs[]. Say how old the data is (data_as_of) when it is a saved report. "
        + "On error projector_permission_denied or a Projector permission message the user lacks that right in "
        + "Projector (exports need the Export Data permission): say so and do not retry.";

    public const string TimeProjectsSchemaHint =
        "Output keys: work_date, query, count, total, offset, has_more, next_offset, not_chargeable_hidden, projects[] (project_code, "
        + "project_name, engagement_code, client_name, billable, chargeable, roles[] with role_uid and role_name, last_used, "
        + "hours_last_30d, recent_tasks[] and matched_tasks[] with task_path, wbs_code, hours, last_used), recent_note, next_step. "
        + "Save by the wbs_code of a recent task.";

    public const string TimecardOptionsSchemaHint =
        "Output keys: work_date, project (open_for_time, narrative_required), roles[] (role_uid, role_name), "
        + "no_role (set when roles[] is empty: save_timecard is refused on this project until the project manager "
        + "named there adds the user), "
        + "rate_types[] (the set most tasks allow), task_query, "
        + "tasks[] (task_uid, task_name, task_path, wbs_code, task_type, rate_types[] only when that task differs from the top-level set, "
        + "default_rate_type, assigned), tasks_count, tasks_total, tasks_has_more, tasks_next_offset, "
        + "tasks_summary_hidden (summary tasks with sub-tasks are never listed: Projector rejects time on them), "
        + "assignment_note (on projects where only assigned people can post time: tasks with assigned = false are "
        + "rejected at submit, so prefer assigned = true), "
        + "rules (time_increment_minutes, max_hours_per_day, location_required, udf1, udf2), next_step.";

    public const string SaveTimecardSchemaHint =
        "Output keys: action (saved|dry_run), results[] (one per card, in input order: index, status saved|valid|invalid|"
        + "failed|not_attempted, action created|updated, timecard (timecard_uid, work_date, hours, project_code, task, "
        + "task_path, wbs_code, role, rate_type, narrative, status), day, note, warnings; or error and message), days[] "
        + "(work_date, total_hours, card_count: all the user's cards on that date after this call), saved_count, "
        + "valid_count (dry run), invalid_count, failed_count, not_attempted_count, submitted (always false), note. "
        + "Relay each card's status, errors and warnings (e.g. a possible duplicate) to the user. "
        + "invalid = not sent (fix and send it again); failed = Projector refused it; not_attempted = not sent because an "
        + "earlier card's outcome was unknown. "
        + "Error write_outcome_unknown on a card: that save may or may not have happened; check list_timecards before "
        + "sending it or the not_attempted cards again. "
        + "On error web_services_access_view_only nothing was saved: tell the user their Projector Web Services Access is "
        + "V (View), not U (Update), and to ask their Projector PSA administrator to change it; do not retry. "
        + "On error summary_task or not_assigned_to_task nothing was saved: pick one of the sub-tasks the message lists, "
        + "or a task with assigned = true, or ask the user. "
        + "On error projector_busy nothing was saved: retry once after a few seconds.";

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
