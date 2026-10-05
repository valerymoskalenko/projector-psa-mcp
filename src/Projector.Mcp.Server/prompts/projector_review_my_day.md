# projector_review_my_day

**Name:** `projector_review_my_day`
**Arguments:** `work_date` (optional, `yyyy-MM-dd`; default today, or the previous working day before 06:00)
**Description:** Reviews the signed-in user's working day and proposes the missing Draft time cards. Saves only what the user approves, in one call. Never submits.

## When to use

"Review my time for today", "what did I miss on my time sheet yesterday?", "fill in my time cards for Monday".

## Steps

1. **Projector** (all tools default to the signed-in user):
   - `list_timecards` for the day: every card, Rejected included, and whether the user can fix it.
   - `list_timecards` with `group_by: "task"` from two weeks before the day through the day, in one call: one row per task with its last description, as history; `by_date` has `expected_hours` per day and `short_by` on working days below expected (days without cards included).
   - `get_schedule` only to name a holiday or PTO.
   - `list_time_projects`: chargeable projects, most recently used first, with recent tasks; `query` also matches recent task names and descriptions.
2. **Evidence** from whatever sources the client can access:
   - calendar and Teams meetings (a transcript shows attendance and the real length; never quoted)
   - mail and chat messages the user wrote, edited files, work items, commits and pull requests

   Received-only mail, notifications, placeholder blocks and earlier AI summaries are not evidence.
3. **Compare** evidence with the posted cards: covered, missing, duplicates, wrong project, Rejected.
4. **Map** each missing activity:
   - history first, then `get_timecard_options` (with `query`)
   - summary tasks are never offered; tasks marked `assigned: false` are avoided
   - the rate type is always the task's default
   - durations come from transcripts or the calendar; a run of the user's own messages or commits becomes a "suggested" block; single messages become questions
5. **Propose**, in sections:
   - A: summary
   - B: covered activities and fixes
   - C: numbered cards
   - D: questions
   - E: not confirmed

   Then wait for approval. Answers to the questions are not approval: apply them, show the final numbered list and ask once "Save these N cards?"; save only after a yes (also for changes to existing cards).
6. **Save** all approved cards in one `save_timecard` call (`cards`, WBS code as `task`). Report each card's status and the day totals.
7. **Check**: read the day back with `list_timecards`; every approved card is there once with its hours, task and description, and the day total matches.

## Notes

- Clients differ in what evidence they can reach: Microsoft 365 Copilot needs the agent's Email, Meetings, Teams messages and OneDrive/SharePoint capabilities; coding agents can add local git history.
- Personal mapping rules (for example "internal calls go to Team Meetings") belong in the user's own instructions, not in this prompt.
- For logging one known entry, use `projector_log_time` instead.
