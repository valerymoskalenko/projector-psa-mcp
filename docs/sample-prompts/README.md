# Sample prompts

Two long prompts that have been used for real work. The server sends them as MCP prompts. Clients with MCP prompt support (Claude Code, Claude Desktop, VS Code) offer them as slash commands with the arguments below. In any other chat connected to this server, open the file, fill in its input lines and paste it.

| Prompt (file) | What it does |
|---|---|
| [`projector_review_my_day`](../../src/Projector.Mcp.Server/prompts/projector_review_my_day.md) | Completes one working day of time cards. It collects evidence of the day's work (calendar, meetings, mail, chats, commits), compares it with the cards already posted and proposes the missing ones. You approve; it saves Drafts and reads the day back. |
| [`projector_expense_report`](../../src/Projector.Mcp.Server/prompts/projector_expense_report.md) | Builds a Draft expense report for one trip, with every receipt attached. It finds the project from the trip name, shows a dry run as a numbered table, saves after you write "save" and reads the report back. |

Both prompts only create Drafts. They never submit, approve or delete anything; you submit in Projector.

## Arguments

`projector_review_my_day` (all optional):

| Argument | Input line | Without it |
|---|---|---|
| `work_date` | Day (`yyyy-MM-dd`) | Today, or the previous working day before 06:00 |
| `rules_file` | Rules file: path or link to your time-rules file | No personal rules |
| `code_folders` | Code folders with your local git repositories | Only the folders your rules file lists |

`projector_expense_report`:

| Argument | Required | Input line |
|---|---|---|
| `trip_name` | yes | Trip name: the customer or purpose, e.g. "Trip to Toronto, Contoso ERP rollout". It is the report name, and the project is found from it |
| `trip_city` | yes | City |
| `trip_first_day` | yes | First day (`yyyy-MM-dd`) |
| `trip_last_day` | yes | Last day (`yyyy-MM-dd`, not before the first day) |
| `receipts` | yes | Receipt files: a folder path or a OneDrive/SharePoint link |
| `trip_country` | no | Country |
| `receipt_mail` | no | Receipt mail: a mailbox or mail folder |
| `project_code` | no | Project; without it the prompt finds it from the trip name |
| `shared_meals` | no | Shared meals: your items per meal |
| `statement` | no | Bank or card statement |

An argument that is not given is sent as "(not given)"; the prompt then asks you when it needs the value.

## Your own rules

The time prompt reads a personal rules file first, if you keep one (for example `MyTimeRules.md`). Keep it private: it names your projects, tasks, colleagues and folders. A useful layout:

```markdown
# My time-entry rules

## Always
- Rules that apply to every card (description style, which task to prefer).

## Durations and merging
- How to size short work (a single e-mail, a run of chats on one topic).

## Leave out silently
- Calendar blocks, notifications and chats that are never time.

## Mappings
| Activity | Project > task (WBS) | Since |
|---|---|---|
| All-employee meetings | <project code> <project name> > <task> (WBS n) | <date> |

## Evidence
- Where your local git repositories are; service accounts whose commits are yours.
```

Add a rule whenever you correct a proposal the same way twice.
