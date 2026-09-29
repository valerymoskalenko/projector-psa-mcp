# projector_log_time

**Name:** `projector_log_time`  
**Description:** Logs the signed-in user's own work time as a Draft time card. Never submits.

## When to use

User says "log 2 hours on Contoso yesterday for the data migration" or "fix my rejected time card".

## Tools

1. **`list_time_projects`** with `work_date` (and `query` from the user's words) → `project_code` and the user's role(s).
2. **`get_timecard_options`** with `project_code` and `work_date` (add `query` with part of the task name, WBS code or parent name on big projects) → tasks that take time, with `task_path` and `default_rate_type`, rules (time increment, UDFs, location). Summary tasks (with sub-tasks) are never listed. Where the project allows time only on assigned tasks, pick a task with `assigned: true`.
3. Show the user date, hours, project, task path, role and narrative; wait for an explicit yes. The rate type is not a choice: the server uses the task's default.
4. **`save_timecard`** with `cards`: every card the user approved, in one call (one approval). Each card has all required fields; `task` is best given as the WBS code (it also accepts the UID, the `task_path` or a unique name); role accepts the UID or the exact name. Add `dry_run: true` to check the cards and see the day totals without saving.
5. Report from the result, card by card: its `status` (saved, invalid, failed, not_attempted), error or warnings, and the day totals from `days`.

To change an existing card: **`list_timecards`** (every status, Rejected included; `editable: true` = Draft or Rejected) → take `timecardUid` (and the current task/role UIDs) → `save_timecard` with `timecard_uid` and the full card. The rate type is reset to the task's default.

## Notes

- Own time sheet only; there is no resource parameter.
- Task names repeat under different parents (many "Development" tasks); use the `task_path` or WBS code, not the bare name.
- `list_time_projects`: pick projects with `chargeable: true`; Projector refuses time where the user has no role.
- Creates Draft cards; updates only Draft or Rejected cards. Saving a Rejected card makes it a Draft again; the user resubmits it in Projector.
- Never submits, approves or deletes. Tell the user to submit in Projector.
- Hours must fit the account's time increment (e.g. 15 minutes = multiples of 0.25).
- `write_outcome_unknown`: the save may or may not have happened. Check `list_timecards` before trying again.
- `no_default_rate_type`: the task has no default rate type in Projector and more than one is possible; nothing was saved. Tell the user to enter that card in Projector.
- `summary_task` / `not_assigned_to_task`: nothing was saved; Projector would reject the card at submit. Pick one of the sub-tasks the message lists, or a task with `assigned: true`, or ask the user.
- `projector_busy`: too many requests ran at once; nothing was saved. Retry once after a few seconds.
- `web_services_access_view_only`: nothing was saved. The user's Projector **Web Services Access** is **V** (View), not **U** (Update); tell them to ask their Projector PSA administrator to change it. Don't retry.
