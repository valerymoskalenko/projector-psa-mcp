# Sample prompts

Long prompts to paste into any AI chat that is connected to this server (Claude, Microsoft 365 Copilot, VS Code and others). They do the same work as the server's built-in MCP prompts, but you can read them, change them and keep your own copy.

| Prompt | What it does | Built-in MCP prompt |
|---|---|---|
| [SubmitMyTime2ProjectorPSA.md](SubmitMyTime2ProjectorPSA.md) | Completes one working day of time cards. It collects evidence of the day's work (calendar, meetings, mail, chats, commits), compares it with the cards already posted and proposes the missing ones. You approve; it saves Drafts and reads the day back. | [`projector_review_my_day`](../../src/Projector.Mcp.Server/prompts/projector_review_my_day.md) |
| [SubmitExpenses2ProjectorPSA.md](SubmitExpenses2ProjectorPSA.md) | Builds a Draft expense report for one trip, with every receipt attached. It shows a dry run as a numbered table, saves after you write "save" and reads the report back. | [`projector_expense_report`](../../src/Projector.Mcp.Server/prompts/projector_expense_report.md) |

Both prompts only create Drafts. They never submit, approve or delete anything; you submit in Projector.

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
