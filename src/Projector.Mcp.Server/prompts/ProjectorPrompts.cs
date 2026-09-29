using System.ComponentModel;
using Microsoft.Extensions.AI;
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
        "Returns the signed-in user's timecards for a date window (resolve me via email first).")]
    public static ChatMessage MyTimecards(
        [Description("Signed-in user email")] string email,
        [Description("Start date yyyy-MM-dd")] string start_date,
        [Description("End date yyyy-MM-dd")] string end_date)
        => new(ChatRole.User,
            $"What are my time cards from {start_date} to {end_date}? " +
            $"Resolve me with get_resource email={email}, then list_timecards. " +
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
            "if several fit, ask me; (3) show me date, hours, project, task path, role and narrative and wait for my yes; " +
            "(4) save_timecard with cards = [this card] (WBS code as task); then tell me the card's status, the day's total hours and any warnings. " +
            "It saves a Draft only; tell me to submit in Projector. " +
            TimeEntryToolService.NoSaveToolHint + " " +
            "To change an existing Draft or Rejected card, get its timecardUid from list_timecards and send the full card in save_timecard cards.");

    [McpServerPrompt(Name = "projector_review_my_day"), Description(
        "Reviews the signed-in user's working day: collects evidence of the work (meetings, mail, chats, files, work " +
        "items) from the sources the client can access, compares it with the cards already posted, proposes the missing " +
        "Draft time cards with project, task and hours, and saves the approved ones in one call. Never submits.")]
    public static ChatMessage ReviewMyDay(
        [Description("Work date yyyy-MM-dd; omit for today (before 06:00: the previous working day)")] string? work_date = null)
        => new(ChatRole.User,
            (string.IsNullOrWhiteSpace(work_date)
                ? "Review my Projector PSA time for today (before 06:00 use the previous working day; say which date you used). "
                : $"Review my Projector PSA time for {work_date.Trim()}. ") +
            ReviewMyDaySteps);

    /// <summary>The daily review, generic for any MCP client (evidence from whatever sources it can reach).</summary>
    internal const string ReviewMyDaySteps =
        "Read-only until I approve entries. Time cards are a date and hours: count each activity on my local working day. " +
        "1) Projector (tools default to me): list_timecards for the day (every status; editable = false means only I can fix it " +
        "in Projector); get_schedule for my expected hours, holidays and PTO; my last 10 working days with list_timecards, one " +
        "week per call, as history (by_date gives the posted hours per day: flag days below expected); list_time_projects for " +
        "the day (chargeable projects, most recently used first, with my recent tasks; its query also matches recent task names " +
        "and descriptions). " +
        "2) Evidence of the day's work from every source you can access: my calendar and Teams meetings (a meeting transcript, " +
        "when there is one, shows whether I attended and how long it really ran; never quote it), e-mails and chat messages I " +
        "wrote, files I edited, work items, commits and pull requests. Not evidence: received-only mail, notifications, my own " +
        "placeholder blocks, messages of only a few words, earlier AI summaries. A meeting chat saying it was cancelled means it " +
        "didn't happen. Without a transcript, use the calendar time and say \"attendance not verified\". " +
        "3) Compare: mark each activity covered by an existing card or missing; flag duplicates, wrong projects and Rejected cards. " +
        "4) Map each missing activity: history first (the task of my most recent card for the same topic, customer or meeting " +
        "series; conflicting history becomes a question), then get_timecard_options for the project (query = topic words, a " +
        "ticket number or a WBS code). Summary tasks are never listed; where tasks show assigned, pick assigned = true. The rate " +
        "type is always the task's default: never ask about it. Show the full task path and WBS code everywhere. Durations: " +
        "meetings from the transcript or calendar; a run of my own messages or commits on one topic is a \"suggested\" block from " +
        "first to last evidence, rounded to the nearest time increment and trimmed at meetings; a single message or an unmeasured " +
        "call is a question, not an estimate. Never invent a project, task or duration, and never pad the day. Descriptions on " +
        "billable projects use customer terms. " +
        "Output: A) summary (expected, posted, proposed, gap); B) covered activities, duplicates, Rejected cards and cards to " +
        "fix; C) numbered proposals: hours | project | task path (WBS) | role | description | evidence | confidence; D) numbered " +
        "questions, one per possible card, with options; E) what could not be confirmed and why. Then stop and ask which entries " +
        "to save. " +
        "5) Save all approved cards in one save_timecard call (cards = [...], WBS code as task). Report each card's status " +
        "(saved, invalid, failed, not_attempted) with its reason, and the day totals against my expected hours. Fix invalid cards " +
        "with me and send them in one more call. Cards are Drafts; I submit in Projector. " +
        TimeEntryToolService.NoSaveToolHint;

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
