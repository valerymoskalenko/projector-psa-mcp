# projector_resource_bookings

**Name:** `projector_resource_bookings`  
**Description:** Answers what a person is booked on for a date window and which projects they are assigned to. Does not answer timecard totals or dollar budgets.

## When to use

User asks “what is the booking for X this week?”, “list projects they are assigned to”, or Resourcing Dashboard-style load by project.

## Example prompt

> What is booking for Tiago Cavalca for the last week of September? List all projects where he is assigned to.

## Tools

1. **`get_resource`** with `full_name` or `id` (preferred) or `email` (slower; never pass email as `ResourceReferenceSystemId` into schedule tools).
2. **`get_schedule`** for the week window (`start_date` / `end_date`).

## Notes

- Answer from **schedule** `roles` (assignments with engagement/client/managers) and `bookings` (scheduled minutes/hours + notes), not `list_timecards`.
- Use top-level `days` / `weeks` for capacity summaries (booked vs available); they are not timecards.
- Include Time Off / Holiday rows when present; they are not project codes.
- Sum `scheduledMinutes` (or `scheduledHours`) for project bookings when reporting weekly booked hours.
- Prefer this over `check_availability` when the user wants a project list; use availability when they ask about free capacity or “can they take N hours/week”.
- **Direction:** this is **person → projects**. For **project → people** (roster) use `projector_project_roles`; for **project → people with hours** use `projector_project_bookings` / `projector_teammates_on_persons_projects`.
