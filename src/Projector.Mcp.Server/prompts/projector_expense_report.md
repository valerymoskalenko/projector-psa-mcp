<!-- The MCP prompt projector_expense_report: the server sends this text, with the Trip lines filled from the prompt's arguments. To use it without the server's prompt support, fill in the Trip lines and paste it into any chat connected to the Projector PSA MCP server. -->
Create a draft Projector PSA expense report for one trip, with every receipt attached, so I only have to review it and press Submit in Projector. You prepare and save; I submit. Never submit, approve or delete anything.

Use the Projector PSA tools (list_expenses, save_expenses) and every source of my receipts you can reach (files, mail, OneDrive/SharePoint).

Trip (I fill this in; a line left as <...> or "(not given)" is unknown: ask me if you need it):
- Trip name: <customer or purpose of the trip, e.g. "Trip to Toronto, Contoso ERP rollout"; it is the report name and identifies the project>
- City: <city>
- Country: <country>
- First day: <yyyy-MM-dd>
- Last day: <yyyy-MM-dd>
- Project: <project code, or empty to find it from the trip name>
- Receipt files: <folder path or OneDrive link>
- Receipt mail: <mailbox or mail folder> (airline, hotel, taxi, Uber or car rental, restaurants)
- Shared meals: <for each meal, the items that are mine>
- Bank or card statement: <file, optional>

Steps:

1. **Receipts**. Read every file and every receipt e-mail in the trip window, from two days before the first day through two days after the last. For each receipt note: date of the expense, merchant, amount, currency, what it is, and the file or e-mail it comes from. A file can hold both an invoice and a receipt; a hotel folio and its payment slip are one expense, not two. Projector takes PDF, PNG, JPEG or GIF files up to 2 MB. A receipt that exists only as an e-mail body, or as a file Projector won't take, goes on the open-questions list ("save this e-mail as PDF"), not into a card. Ask me if can't find file with receipt. Each expense card must have a receipt and/or invoice.

2. **Statement** (only if I gave one). Match each trip charge to a receipt; a foreign-currency charge matches at about the card's rate (within 3%). List charges with no receipt, and receipts with no charge. Don't guess what a charge is; ask me about the ones you can't place.

3. **Projector options**. Call list_expenses with include_options = true and options_date = the trip's first day:
   - Project: use the code I gave. If I gave none, find it from the trip name: pick from options.projects (call list_expenses with query = the customer or purpose words of the trip name) only a project that is open on the trip dates and allows the expense types I need, and whose client, engagement or name matches the trip name. If more than one fits, or none, ask me. Never invent a project code.
   - Expense types, locations, currencies, report currency, rules (receipt_max_kb, location_required) and closed_days come from the options; use those names exactly.
   - Style: call list_expenses (no report) and open my most recent trip report (list_expenses with report = its ER number). Copy how I split expenses (for example one hotel card per stay or per night), my expense types per kind of expense and my description wording.
   - The report name is the trip name. If a Draft report with this name already exists, add to it (report = its ER number) instead of creating a second one.

4. **Dry run**. Call save_expenses with dry_run = true and every card (1-20 cards per call). Each card: date as on the receipt, project_code, expense_type, amount and currency as on the receipt (Projector converts with its own rate; don't convert yourself), location, and a description in this form: "<What> - <amount> <CUR> (<amount in report currency> <report currency>)", for example "Dinner with Contoso team - 84.20 CAD (61.35 USD)". Take the converted amount from the dry run's amount_report_currency, not from your own math. For a shared meal, the amount is only my items, and the description names them.

   Then show me, in the chat:
   A. A numbered table: # | Date | Expense type | Description | Amount and currency | Amount in report currency | Location | Receipt file or e-mail.
   B. The total in the report currency, and the number of cards.
   C. Warnings from the dry run (receipt required, invalid card), word for word.
   D. Open questions, numbered: charges without a receipt, receipts I must save as PDF, a project or expense type you could not choose.
   Then stop and wait. My answers to D are not approval: apply them and show the final table again. Save only after I write "save".

5. **Save** (after "save"):
   - Attach every receipt. Choose the way your client supports, in this order, and tell me which one you use:
     - Upload the file from disk into my receipt pool with the URL, ticket and command in options.receipt_upload (`how`), one file per request, and note each receipt_uid. Keep the quotes around the path: file names with commas fail without them. A ticket lasts 30 minutes; call list_expenses with include_options again for a new one.
     - receipt.source_url: a public https link that downloads the file.
     - receipt.receipt_uid of a receipt already in my pool (options.receipt_pool), for example one I uploaded in Projector myself.
     - receipt.content_base64 with file_name, only for a file under about 10 KB.
     If none of these works for a file, don't save its card yet: ask me to upload it to my receipt pool in Projector, then call list_expenses with include_options again. Don't change, rename or move my files.
   - Call save_expenses with the cards exactly as approved, each with its receipt, and report_name = the trip name (or report = the ER number), with brief = true. Up to 20 cards per call; for more, save the rest into the same report (report = the ER number from the first result).
   - If a result is write_outcome_unknown or failed, read the report with list_expenses before trying again, so no card is saved twice.
   - To correct a saved card later, send its card_uid with only the fields to change; its other fields and receipts stay.

6. **Check**. Don't trust the save result alone: read the report back with list_expenses (report = the ER number) and confirm that
   - every card I approved is there, once, with the right date, type, amount and description;
   - no card has missing_receipt = true, and every receipt is linked to its card;
   - the report total equals the total I approved.
   Report the ER number, the total, the card count, every warning, and receipts still in the pool that are not linked to a card. Then tell me to review and submit the report in Projector.

**Rules**:
- Use only what my receipts and my answers show. Never invent a receipt, amount, date, merchant or project; ask me instead.
- Keep my wording in descriptions where I gave it.
- An expense with no receipt gets no card until I decide; list it under D.
- Personal items (minibar, personal shopping) stay out unless I say otherwise.
