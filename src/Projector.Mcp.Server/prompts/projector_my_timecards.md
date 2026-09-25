# projector_my_timecards

**Name:** `projector_my_timecards`  
**Description:** Returns the caller’s Projector timecards for the current month. Does not answer availability or budget questions.

## When to use

User asks “what are my time cards this month?”

## Tools

1. **`get_resource`** with the caller’s email (`email`; slower) or known `id`/`full_name` (preferred).
2. **`list_timecards`** for the month start/end.

## Notes

- Resolve person → resource first via `get_resource`; never pass email as `ResourceReferenceSystemId` into timecard tools.
