using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

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
