# projector_expense_report

**Name:** `projector_expense_report`
**Arguments:** `trip` (purpose, place, first and last day), `receipts` (folder, cloud link or mailbox), optional `report_name`, `project_code`, `statement`
**Description:** Builds a Draft expense report for one trip from the user's receipts, with every receipt attached. Dry run first; saves only after the user says "save"; reads the report back. Never submits.

## When to use

"Create my expense report for the Toronto trip", "add my trip receipts to Projector".

## Steps

1. **Receipts**: every receipt file and receipt e-mail from two days before the trip through two days after; date, merchant, amount, currency and source per receipt. Only PDF, PNG, JPEG or GIF up to 2 MB can be attached; anything else becomes a question.
2. **Statement** (optional): each trip charge matched to a receipt; unmatched charges become questions.
3. **`list_expenses`** with `include_options: true` and `options_date` = the trip's first day: project, expense types, locations, currencies, rules, receipt pool and `receipt_upload`. The user's most recent trip report sets the card split and description style. An existing Draft report for the trip is reused.
4. **`save_expenses`** with `dry_run: true` (at most 20 cards per call): amounts in the receipt's currency, converted by Projector. The user sees a numbered table, the total, the warnings and the open questions, then says "save". Answers to questions are not approval.
5. **Save**: the client attaches each receipt the way it can (upload via `receipt_upload` then `receipt_uid`, `source_url`, a pool `receipt_uid`, or `content_base64` for files under about 10 KB) and says which; then `save_expenses` with `brief: true`.
6. **Check**: `list_expenses` with `report`: every card once, no `missing_receipt`, the total matches. The user submits in Projector.

## Notes

- Never invents a receipt, amount, date, merchant or project; asks instead.
- After `write_outcome_unknown` or a failure, the report is read before any retry, so no card is saved twice.
- For changing one saved card, `save_expenses` with its `card_uid` and only the fields to change.
