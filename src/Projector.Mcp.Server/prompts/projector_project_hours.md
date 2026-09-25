# projector_project_hours

**Name:** `projector_project_hours`  
**Description:** Sums hours spent on a project/engagement and reports billable/productive flags. Planned hour (and money, when permitted) budgets come from `get_engagement`. Does **not** compute dollar over-budget.

## When to use

User asks total spent hours on project C, or whether work is over budget in **hours** terms vs the planned hour budget.

## Tools

1. Resolve person if needed via **`get_resource`** (`id` / `full_name` preferred; `email` slower).
2. Resolve engagement/project via **`list_engagements`** / **`get_engagement`**.
3. **`list_timecards`** filtered by `project_code` for actual hours.
4. Planned budgets: **`get_engagement`** — use `workHoursTimeBudgetAmount` when present; money fields only if returned for the signed-in user.

## Honest gap

Actual hours vs planned hours is supported. Dollar over-budget / earned vs budget requires Projector Engagement Portfolio Excel export. If money budget fields are omitted, say the user cannot see financial budgets — do not invent $0.
