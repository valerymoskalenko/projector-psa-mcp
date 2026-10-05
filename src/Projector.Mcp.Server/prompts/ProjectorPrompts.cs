using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Projector.Application.Tools;

namespace Projector.Mcp.Server.Prompts;

[McpServerPromptType]
public sealed class ProjectorPrompts
{
    [McpServerPrompt(Name = "projector_availability"), Description(
        "Answers whether one or more people are available for a requested number of hours per week. Does not answer timesheet or budget questions.")]
    public static ChatMessage Availability(
        [Description("Comma-separated emails, display names, or resource ids")] string people,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date,
        [Description("Required hours per week")] double required_hours_per_week = 40)
        => new(ChatRole.User,
            $"Check availability for: {people}. Window {start_date} to {end_date}. " +
            $"Required {required_hours_per_week} hours/week. " +
            "Prefer check_availability. Capacity uses utilization-basis minutes. " +
            "Report states available/partially_available/fully_booked/overallocated/non_working — not UI colors.");

    [McpServerPrompt(Name = "projector_my_timecards"), Description(
        "Returns the signed-in user's timecards for a date window.")]
    public static ChatMessage MyTimecards(
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date,
        [Description("Not needed: the tools default to the signed-in user")] string? email = null)
        => new(ChatRole.User,
            $"What are my time cards from {start_date} to {end_date}? " +
            "Call list_timecards without resource_id (it defaults to me; do not look me up with get_resource). " +
            "Sum workMinutes; do not invent dollar budgets.");

    [McpServerPrompt(Name = "projector_log_time"), Description(
        "Logs the signed-in user's own work time as a Draft time card (never submits). Finds project, task, role and rate type, then confirms before saving.")]
    public static ChatMessage LogTime(
        [Description("Work date yyyy-MM-dd")] string work_date,
        [Description("Hours worked, e.g. 1.5")] double hours,
        [Description("Project name or code, as the user said it")] string project,
        [Description("What was done (the time card narrative)")] string narrative)
        => new(ChatRole.User,
            $"Log {hours} hours on {work_date} for '{project}': {narrative}. " +
            "Steps: (1) list_time_projects with work_date (query = the project words) to find the project_code and my role; " +
            "(2) get_timecard_options with project_code and work_date (add query with task words, a WBS code or the parent's name " +
            "on big projects) to pick a task by its task_path (summary tasks are not listed; where tasks show assigned, use one with assigned = true; the rate type is always the task's default; don't ask me about it); " +
            "if several fit, ask me; (3) show me date, hours, project, task path, role and narrative and wait for my yes. " +
            SaveConfirmationRule + " " +
            "(4) save_timecard with cards = [this card] (WBS code as task); then tell me the card's status, the day's total hours and any warnings. " +
            "It saves a Draft only; tell me to submit in Projector. " +
            TimeEntryToolService.NoSaveToolHint + " " +
            "To change an existing Draft or Rejected card, get its timecardUid from list_timecards and send the full card in save_timecard cards.");

    [McpServerPrompt(Name = "projector_review_my_day"), Description(
        "Completes the signed-in user's time cards for one working day: collects evidence of the work (meetings, mail, " +
        "chats, files, commits) from the sources the client can access, compares it with the cards already posted, proposes " +
        "the missing Draft cards with project, task path and hours, saves only what the user approves and reads the day " +
        "back. Never submits.")]
    public static ChatMessage ReviewMyDay(
        [Description("Work date yyyy-MM-dd; omit for today (before 06:00: the previous working day)")] string? work_date = null,
        [Description("Path or link to the user's own time-rules file (mappings, exclusions); read first")] string? rules_file = null,
        [Description("Folders with the user's local git repositories, scanned recursively for the day's commits")] string? code_folders = null)
    {
        var text = PromptFiles.Fill(PromptFiles.ReviewMyDay,
            ("Day", OptionalDate(work_date, nameof(work_date))),
            ("Rules file", rules_file),
            ("Code folders", code_folders));
        return new(ChatRole.User, text + "\n\n" + TimeEntryToolService.NoSaveToolHint);
    }

    /// <summary>
    /// An agent saved 13 cards after the user had only answered its questions (2026-09-30): answers are not approval.
    /// </summary>
    internal const string SaveConfirmationRule =
        "My answers to your questions are not approval to save: apply them, show me the final numbered list of cards and " +
        "ask once \"Save these N cards?\"; call save_timecard only after I say yes. The same goes for changes to existing cards.";

    [McpServerPrompt(Name = "projector_expense_report"), Description(
        "Builds a Draft expense report for one trip from the user's receipts (files, mail, cloud folders): finds the " +
        "project from the trip name, makes one card per expense with its receipt attached, shows a dry run first, saves only " +
        "after the user writes \"save\", then reads the report back. The trip name is the report name. Never submits.")]
    public static ChatMessage ExpenseReport(
        [Description("Customer or purpose of the trip, e.g. \"Trip to Toronto, Contoso ERP rollout\": the report name, and what identifies the project")] string trip_name,
        [Description("City of the trip")] string trip_city,
        [Description("First day of the trip, yyyy-MM-dd")] string trip_first_day,
        [Description("Last day of the trip, yyyy-MM-dd")] string trip_last_day,
        [Description("Where the receipt files are: a folder path or a OneDrive/SharePoint link")] string receipts,
        [Description("Country of the trip")] string? trip_country = null,
        [Description("Mailbox or mail folder with receipt e-mails (airline, hotel, taxi, restaurants)")] string? receipt_mail = null,
        [Description("Project code; omit to find it from the trip name among the projects open for expenses")] string? project_code = null,
        [Description("For each shared meal, the items that are the user's")] string? shared_meals = null,
        [Description("Bank or card statement to check that no trip charge is missing")] string? statement = null)
    {
        var first = RequiredDate(trip_first_day, nameof(trip_first_day));
        var last = RequiredDate(trip_last_day, nameof(trip_last_day));
        if (string.CompareOrdinal(last, first) < 0)
        {
            throw new McpException($"trip_last_day {last} is before trip_first_day {first}.");
        }

        var text = PromptFiles.Fill(PromptFiles.ExpenseReport,
            ("Trip name", Required(trip_name, nameof(trip_name))),
            ("City", Required(trip_city, nameof(trip_city))),
            ("Country", trip_country),
            ("First day", first),
            ("Last day", last),
            ("Project", project_code),
            ("Receipt files", Required(receipts, nameof(receipts))),
            ("Receipt mail", receipt_mail),
            ("Shared meals", shared_meals),
            ("Bank or card statement", statement));
        return new(ChatRole.User, text);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new McpException($"{name} is required.") : value.Trim();

    private static string RequiredDate(string? value, string name) =>
        OptionalDate(Required(value, name), name)!;

    private static string? OptionalDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _)
            ? value.Trim()
            : throw new McpException($"{name} must be a date yyyy-MM-dd, got '{value.Trim()}'.");
    }

    [McpServerPrompt(Name = "projector_timecards_for_project"), Description(
        "Returns a person's timecards for a named project/engagement in a week or month.")]
    public static ChatMessage TimecardsForProject(
        [Description("Person resource_id, full_name, or email")] string person,
        [Description("Project or engagement code/name")] string project,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"Show time cards for {person} on {project} from {start_date} to {end_date}. " +
            "Resolve person with get_resource; resolve engagement with list_engagements / get_engagement; " +
            "then list_timecards with project_code. Do not invent contract $ amounts.");

    [McpServerPrompt(Name = "projector_project_hours"), Description(
        "Sums hours on a project/engagement and reports billable/productive flags. Planned budgets from get_engagement; no dollar over-budget.")]
    public static ChatMessage ProjectHours(
        [Description("Optional person")] string? person,
        [Description("Project or engagement code")] string project,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"Total hours on {project} from {start_date} to {end_date}" +
            (string.IsNullOrWhiteSpace(person) ? "." : $" for {person}.") +
            " Use get_engagement for billable/productive flags and planned hour/money budgets (money only if present). " +
            "Use list_timecards for actual hours. If money fields are omitted, say financial budgets are not visible — do not say $0. " +
            "Dollar over-budget requires Projector Engagement Portfolio Excel export.");

    [McpServerPrompt(Name = "projector_engagement_budget"), Description(
        "Returns planned hour and (when permitted) money budgets for one engagement. Not actuals vs budget.")]
    public static ChatMessage EngagementBudget(
        [Description("Project or engagement name or code")] string project_or_engagement)
        => new(ChatRole.User,
            $"What is the planned budget for {project_or_engagement}? " +
            "Resolve with list_engagements query then get_engagement by engagementCode. " +
            "Always report planned hour budgets when present. Report money budgets only when those fields are present " +
            "(include timeBudgetMetricLabel and costBudgetMetricLabel, not only letter codes). " +
            "If money fields are omitted, say the signed-in user cannot see financial budgets — do not say the budget is $0. " +
            "These are planned budgets only, not actuals vs budget.");

    [McpServerPrompt(Name = "projector_resource_bookings"), Description(
        "Answers what a person is booked on for a date window and which projects they are assigned to. Person→projects; for project→people use projector_project_roles / projector_project_bookings.")]
    public static ChatMessage ResourceBookings(
        [Description("Person full_name, resource_id, or email")] string person,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"What is the booking for {person} from {start_date} to {end_date}? List projects they are assigned to. " +
            "Use get_resource then get_schedule. Answer from roles and bookings, not timecards. " +
            "For project team roster or teammate hours, use list_project_roles / list_proj_bookings instead.");

    [McpServerPrompt(Name = "projector_project_roles"), Description(
        "Lists who is staffed on a project (assignment roster). Not date-window booked hours.")]
    public static ChatMessage ProjectRoles(
        [Description("Project code e.g. P001234-001")] string project_code)
        => new(ChatRole.User,
            $"Who is staffed on project {project_code}? " +
            "Use list_project_roles only. Do not use get_schedule or check_availability. " +
            "For the task plan (tasks, planned dates, effort hours per role) add include_task_plan = true. " +
            "For booked hours in a date window use list_proj_bookings.");

    [McpServerPrompt(Name = "projector_project_bookings"), Description(
        "Lists resources with booked hours on a project in a date window. Not timecards or roster-only.")]
    public static ChatMessage ProjectBookings(
        [Description("Project code e.g. P001234-001")] string project_code,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"Which resources have bookings on {project_code} from {start_date} to {end_date}? " +
            "Use list_proj_bookings. Sum scheduledHours. Exclude zero-hour roles. Not timecards. " +
            "Quote booking notes (notes[]) with their day when a row has them. " +
            "For assignment roster without hours use list_project_roles.");

    [McpServerPrompt(Name = "projector_teammates_on_persons_projects"), Description(
        "Lists all resources with bookings in a window on a named person's projects (e.g. Carol's October teammates).")]
    public static ChatMessage TeammatesOnPersonsProjects(
        [Description("Person full_name")] string person,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"All resources with bookings from {start_date} to {end_date} on {person}'s active projects. " +
            "Steps: (1) get_resource full_name; (2) get_schedule for that person only → distinct project codes with hours; " +
            "(3) list_proj_bookings for those codes and the same window; (4) group by project; list resource + hours; do not invent people. " +
            "Do NOT call get_overview, list_engagements, get_engagement, or list_project_roles. " +
            "If the user said booked in a month, roles-only is not proof — use bookings. " +
            "Honor searchCoverage on list_proj_bookings: when status is partial, say incomplete and follow suggestion.");
}
