# projector_project_bookings

**Name:** `projector_project_bookings`  
**Description:** Lists resources with booked hours on one or more projects in an inclusive date window. Not timecards or assignment-only roster.

## When to use

User asks which people have bookings on a project in a month/week, or wants scheduled hours by teammate on a project.

## Example prompt

> Which resources have October 2026 bookings on P001234-001?

## Tools

1. **`list_proj_bookings`** with `project_code` / `project_codes`, `start_date=2026-10-01`, `end_date=2026-10-31`.

## Notes

- Sum `scheduledHours` (or `scheduledMinutes` / 60) per resource or per project as needed.
- Zero-hour buckets are omitted by the tool — “October bookings” means people actually booked.
- Not actuals: do not use `list_timecards` for staffing hours.

## Do not use

- **`list_project_roles`** alone when the user said “booked in October” (roles are not date-window proof).
- **`get_schedule`** per teammate — slower and hits rate limits on large teams.

## Cross-link

- Person → projects: **`projector_resource_bookings`**.
- Roster without hours: **`projector_project_roles`**.
- Teammates on someone’s projects in a month: **`projector_teammates_on_persons_projects`**.
