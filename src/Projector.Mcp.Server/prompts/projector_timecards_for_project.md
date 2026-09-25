# projector_timecards_for_project

**Name:** `projector_timecards_for_project`  
**Description:** Returns a person’s timecards for a named project/engagement in a week or month. Does not compute missing timesheets or dollar budgets.

## When to use

User asks for time cards for person X on project/engagement Y for this week, previous week, or this month.

## Tools

1. **`get_resource`** if the person is given as email (`email`, slower), exact display name (`full_name`), or system id (`id`).
2. **`list_engagements`** / **`get_engagement`** if only a project name is known (projects hang under engagements).
3. **`list_timecards`** with `resource_id`, date range, and `project_code`.

## Notes

- End users say “project”; API entities are engagement + project code.
- Sum `workMinutes` for totals; do not invent contract $ amounts.
