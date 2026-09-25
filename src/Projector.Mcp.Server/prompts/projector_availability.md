# projector_availability

**Name:** `projector_availability`  
**Description:** Answers whether one or more people are available for a requested number of hours per week. Does not answer timesheet or budget questions.

## When to use

User asks about availability, booking load, free capacity, or whether people can take N hours/week.

## Tools

1. Prefer **`check_availability`** with the people list, date window, and `required_hours_per_week`.
2. Use **`get_schedule`** only for raw single-person schedule drill-down.

## Notes

- End users say “user”; treat that as a **resource**. `check_availability` resolves emails/names via resource APIs; for a single person prefer `get_resource` (`id`/`full_name` preferred, `email` slower).
- Capacity uses **utilization-basis minutes**, not raw working minutes.
- Report explicit states (`available`, `partially_available`, `fully_booked`, `overallocated`, `non_working`), not UI colors.
- Project assignment / booking lists: see [`projector_resource_bookings.md`](projector_resource_bookings.md).
