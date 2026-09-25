# projector_project_roles

**Name:** `projector_project_roles`  
**Description:** Lists who is staffed on one or more projects (assignment roster). Does not prove hours in a date window.

## When to use

User asks “who is on project X?”, “who is staffed on …?”, or wants a team roster by project code/name.

## Example prompt

> Who is staffed on Contoso ERP Rollout (P001234-001)?

## Tools

1. **`list_project_roles`** with `project_code` / `project_codes` (1–100).

## Do not use

- **`get_schedule`** or **`check_availability`** — those are person-shaped, not project roster.
- **`list_proj_bookings`** — only when the user needs booked *hours* in a date window (e.g. “booked in October”).
- **`get_engagement`** / **`list_engagements`** — managers and codes only; roles are excluded.

## Cross-link

- Person → projects: **`projector_resource_bookings`** (`get_resource` + `get_schedule`).
- Project → people with hours: **`projector_project_bookings`**.
