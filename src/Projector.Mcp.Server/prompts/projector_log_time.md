# projector_log_time

**Name:** `projector_log_time`  
**Description:** Logs the signed-in user's own work time as a Draft time card. Never submits.

## When to use

User says "log 2 hours on Contoso yesterday for the data migration" or "fix my rejected time card".

## Tools

1. **`list_time_projects`** with `work_date` (and `query` from the user's words) → `project_code` and the user's role(s).
2. **`get_timecard_options`** with `project_code` and `work_date` (add `query` with part of the task name, WBS code or parent name on big projects) → open tasks with `task_path`, the rate types, rules (time increment, UDFs, location).
3. Show the user date, hours, project, task path, role, rate type and narrative; wait for an explicit yes.
4. **`save_timecard`** with all required fields. Task accepts the UID, the `task_path`, the WBS code or a unique name; role and rate type accept the UID or the exact name.
5. Report from the result: the saved card, `day.total_hours` for that date, and any `warnings` (for example a possible duplicate).

To change an existing card: **`list_timecards`** → take `timecardUid` (and the current task/role/rate type UIDs) → `save_timecard` with `timecard_uid` and the full card.

## Notes

- Own time sheet only; there is no resource parameter.
- Task names repeat under different parents (many "Development" tasks); use the `task_path` or WBS code, not the bare name.
- `list_time_projects`: pick projects with `chargeable: true`; Projector refuses time where the user has no role.
- Creates Draft cards; updates only Draft or Rejected cards. Saving a Rejected card makes it a Draft again; the user resubmits it in Projector.
- Never submits, approves or deletes. Tell the user to submit in Projector.
- Hours must fit the account's time increment (e.g. 15 minutes = multiples of 0.25).
- `write_outcome_unknown`: the save may or may not have happened. Check `list_timecards` before trying again.
- `web_services_access_view_only`: nothing was saved. The user's Projector **Web Services Access** is **V** (View), not **U** (Update); tell them to ask their Projector PSA administrator to change it. Don't retry.
