# projector_engagement_budget

**Name:** `projector_engagement_budget`  
**Description:** Returns planned hour and (when permitted) money budgets for one engagement. Does not compare actuals to budget or list over-budget projects.

## When to use

User asks what hours or dollars are **budgeted** (planned) on a named project/engagement.

## Tools

1. **`list_engagements`** with `query` when only a name is known (e.g. Boyer – AWP). Honor `searchCoverage` — if partial, say so.
2. **`get_engagement`** with `code` = `engagementCode` from the list (never pass a display name as code).

## How to answer

- Always report **planned hour budgets** when present: `workHoursTimeBudgetAmount` / `workMinutesTimeBudgetAmount`, and chargeable hours if present.
- Report **money budgets** only when fields are present (`currencyCode`, `timeBudgetMetric` / `timeBudgetMetricLabel`, contract/billing-adjusted/RDC amounts, `costBudgetMetric` / `costBudgetMetricLabel` and matching cost amount). Prefer the label fields over letter codes when speaking to the user.
- If money fields are omitted (`budgetVisibility` is `hours_only` or money keys missing): say the signed-in user cannot see financial budgets in Projector — **do not** say the budget is $0.
- These are **planned** budgets only. Over-budget or earned-vs-budget → tell the user to use Projector Engagement Portfolio Excel export.

## Example user prompts

1. How many hours are budgeted on Boyer – AWP?
2. How many hours are budgeted on engagement E00xxxx?
3. What is the contract revenue time budget for Boyer – AWP?
4. What cost budget does Boyer – AWP use?
5. How do my hours last month on Boyer – AWP compare to the hour budget? (pair with `list_timecards` for actual hours vs planned hours — not $ over-budget)
