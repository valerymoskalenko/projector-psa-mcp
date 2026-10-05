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
        + "hours_by_status, expected_hours, short_by: posted hours per day against the schedule; working days without cards "
        + "are listed with 0 hours, so short_by shows the missing days without get_schedule), expected_note (why "
        + "expected_hours is missing: ranges over 56 days, or a status, project_code or query filter), timecards[], searchCoverage. "
        + "Each card has taskName, taskPath (parent tasks > task), taskWbsCode, timecardUid, projectTaskUid, projectRoleUid "
        + "and projectRateTypeUid (use them with save_timecard). Every status is listed, Rejected included (with the "
        + "rejection reason when Projector gives one). The signed-in user's own cards have editable: true = Draft or "
        + "Rejected (save_timecard with timecard_uid can fix it), false = fix it in Projector. "
        + "compact=true returns short cards (workDate, workHours, projectCode, projectName, taskPath, taskWbsCode, roleName, "
        + "rateTypeName, status, description, rejectedReason, timecardUid, editable), about half the size. "
        + "group_by=task returns tasks[] (project_code, project_name, task_path, wbs_code, role_name, rate_type_name, "
        + "card_count, hours, first_date, last_date, last_description; most recent first) and tasks_count instead of "
        + "timecards[]: use it for history reads (task choice and description style). "
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
        + "only), recent_report_runs[] (name, status, completed), note. For the next part pass next_cursor as the argument cursor. Without a dataset: datasets[] and "
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
        + "task_query, "
        + "tasks[] (task_uid, task_name, task_path, wbs_code, task_type, default_rate_type, assigned), tasks_count, tasks_total, tasks_has_more, tasks_next_offset, "
        + "tasks_summary_hidden (summary tasks with sub-tasks are never listed: Projector rejects time on them), "
        + "assignment_note (on projects where only assigned people can post time: tasks with assigned = false are "
        + "rejected at submit, so prefer assigned = true), "
        + "rules (time_increment_minutes, max_hours_per_day, location_required, udf1, udf2), next_step.";

    public const string SaveTimecardSchemaHint =
        "Output keys: action (saved|dry_run), results[] (one per card, in input order: index, status saved|valid|invalid|"
        + "failed|not_attempted, action created|updated, timecard (timecard_uid, work_date, hours, project_code, task, "
        + "task_path, wbs_code, role, rate_type, narrative, status), day, note, warnings; or error and message), days[] "
        + "(work_date, total_hours, card_count: all the user's cards on that date after this call; expected_hours from "
        + "the user's schedule), saved_count, valid_count (dry run), invalid_count, failed_count, not_attempted_count, "
        + "submitted (always false), note, warnings (for the whole call, e.g. the schedule could not be read). "
        + "Relay each card's status, errors and warnings to the user: a possible duplicate, a weekend or day off, a "
        + "holiday, PTO, more hours than the day expects, a date near or after the end of the project or of the user's "
        + "role. Warnings never stop a save. "
        + "invalid = not sent (fix and send it again); failed = Projector refused it; not_attempted = not sent because an "
        + "earlier card's outcome was unknown. "
        + "Error write_outcome_unknown on a card: that save may or may not have happened; check list_timecards before "
        + "sending it or the not_attempted cards again. "
        + "On error web_services_access_view_only nothing was saved: tell the user their Projector Web Services Access is "
        + "V (View), not U (Update), and to ask their Projector PSA administrator to change it; do not retry. "
        + "On error summary_task or not_assigned_to_task nothing was saved: pick one of the sub-tasks the message lists, "
        + "or a task with assigned = true, or ask the user. "
        + "On error projector_busy nothing was saved: retry once after a few seconds.";

    public const string ExpensesSchemaHint =
        "Output keys: resource_id, reports (count, months, rows[] (report, name, status, dates, card_count, total, "
        + "reimbursement when it differs, currency, projects, editable), can_create) without report; report (report, "
        + "name, status, person, currency, total, card_count, editable, locked_reason, cards[] (card_uid, date, "
        + "expense_type, description, amount, currency, rate, amount_report_currency, location, project_code, status, "
        + "editable, rejected_reason, receipts, missing_receipt: true when Projector won't submit it without a receipt), "
        + "report_receipts) with report; options with include_options "
        + "(options_date, projects[] (project_code, name, client, engagement, open, close, expense_types: names or "
        + "\"any\"), projects_total, project_hint (only when project_code or query matched nothing: the projects of the "
        + "user's own time cards in the last 30 days, each open or not open for expenses), projects_has_more, projects_next_offset, expense_types[] (name, group, "
        + "description_required, instructions, receipt_required (true, \"from N USD\" or false), supported), locations, report_currency, currencies, rules "
        + "(receipts_on_cards, receipt_max_kb, receipt_types, non_billable_allowed, outside_project_dates_allowed, "
        + "location_required, entry_for_others_allowed), closed_days, receipt_pool[] (receipt_uid, name, size_kb, "
        + "uploaded), receipt_upload (url, ticket, expires_at, max_kb, types, how: POST a multipart form with ticket, "
        + "file and optional sha256 to url, e.g. curl -sS -F ticket=<ticket> -F \"file=@\\\"<path>\\\"\" <url> (keep the quotes around the path); the "
        + "JSON answer has receipt_uid, name, size_bytes, sha256, warnings, note; the ticket lasts 30 minutes and 50 "
        + "uploads)), note. "
        + "Expense types with supported = false (mileage, per unit) must be entered in Projector. "
        + "On error NoExpenseIdentity: the user has no expense report yet; they create the first one in Projector.";

    public const string SaveExpensesSchemaHint =
        "Output keys: action (saved|dry_run|refused|failed), error, report (number, name, currency, total, card_count, "
        + "status), results[] (one per card, in input order: index, status valid|invalid|saved|failed|not_applied|"
        + "not_attempted, action create|update, card (card_uid, date, project_code, expense_type, description, amount, "
        + "currency, rate, amount_report_currency, location), receipt (name, size_kb, receipt_uid, linked, stored_kb and note when Projector "
        + "re-encoded a photo), errors, warnings), results_omitted (brief only: cards saved without warnings, not listed), cards_total, saved_count, valid_count, invalid_count, failed_count, not_applied_count, "
        + "receipts_in_pool[] (uploaded receipts not linked to a card; attach them later by receipt_uid), submitted "
        + "(always false), note. refused = one or more cards invalid, nothing saved or uploaded: fix them and send the "
        + "call again (only the cards in the call are changed). An error \"Project … is not open for your expenses\" may "
        + "name the projects of the user's time cards on those dates: ask the user which open project to use. Warning \"receipt required\": the card is saved but Projector won't submit the report until it has a "
        + "receipt; ask the user for it. Warning \"receipt size differs\": Projector stored a different number of bytes than were sent; "
        + "ask the user to open the receipt in Projector. not_applied = Projector answered but the card or its receipt is not on the report as sent: tell "
        + "the user to check the report in Projector. "
        + "Error write_outcome_unknown: the save or upload may or may not have happened; check list_expenses with the "
        + "report before sending again. On error web_services_access_view_only nothing was saved: tell the user their "
        + "Projector Web Services Access is V (View), not U (Update); do not retry.";

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
