# projector_teammates_on_persons_projects

**Name:** `projector_teammates_on_persons_projects`  
**Description:** Lists all resources with bookings in a date window on the active projects of a named person (e.g. Carol’s October teammates). Project → people with hours; not person → projects only.

## When to use

User asks for teammates booked on someone’s projects in a month, or “everyone booked on X’s projects in October”.

## Example prompt

> All resources with October bookings on Carol Jones's active projects.

## Tools (strict order — keep SOAP cheap)

1. **`get_resource`** with `full_name=Carol Jones` (or the named person).
2. **`get_schedule`** for that resource only, Oct 1–31 → collect **distinct project codes** that have booked minutes.
3. **`list_proj_bookings`** once for those codes and the **same** window.
4. Group by project; list resource + hours from the payload. **Do not invent people** not returned.

Expected SOAP: about **1 + 1 + ceil(N/3)** WCF calls (N = that person’s project codes in the window).

## Do not use (wrong or wasted SOAP)

- **`get_overview`**
- **`list_engagements`** / **`get_engagement`**
- **`list_project_roles`** when the user said “booked in October” (roles are not October proof)
- Per-teammate **`get_schedule`** or company-wide **`check_availability`**

## Honest gap

`list_project_roles` is a fast roster but **not** an October booking proof. If the user says “booked in October”, skip roles-only and use bookings.

Honor **`searchCoverage`** on `list_proj_bookings` (and any prior list tool): when `status` is `partial`, say coverage is incomplete and follow `suggestion` — never invent missing projects or people.

## Cross-link

- One person’s load: **`projector_resource_bookings`**.
- One project’s hours: **`projector_project_bookings`**.
- Roster only: **`projector_project_roles`**.
