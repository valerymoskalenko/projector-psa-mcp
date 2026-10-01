# projector_my_timecards

**Name:** `projector_my_timecards`  
**Description:** Returns the caller’s Projector timecards for a date window. Does not answer availability or budget questions.

## When to use

User asks “what are my time cards this month?”

## Tools

1. **`list_timecards`** for the window start/end, without `resource_id` (it defaults to the signed-in user).

## Notes

- Do not look the signed-in user up with `get_resource` first: Projector has no “current user” lookup, and the time card, schedule and PTO tools need no resource id for the caller.
