# Projector PSA MCP Server

A read-only [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for [Projector PSA](https://www.projectorpsa.com/). It lets AI assistants — VS Code (GitHub Copilot), Cursor, Claude, and Microsoft 365 Copilot — answer questions about people, schedules, availability, time off, timecards, engagements and project bookings using each user's own Projector permissions.

- **Read-only.** No tool changes data in Projector.
- **Per-user sign-in.** Every user signs in to Projector with OAuth; the server never uses a shared service account.
- **Runs in Azure** (App Service + Azure SQL + Key Vault + Application Insights), or locally for development.

## Tools

| Tool | What it answers |
|------|-----------------|
| `list_resources` | Find people (resources) by name or other text |
| `get_resource` | One person's profile, by id, full name or email |
| `get_schedule` | One person's schedule for a window: working hours, holidays, PTO, bookings |
| `check_availability` | Free capacity for 1–20 people against N hours per week |
| `get_overview` | One-call bundle: profile, timecards, schedule and time off for a person |
| `list_timecards` | Work timecards for a person and date range |
| `list_time_off` | Time-off cards for a person and date range |
| `list_upcoming_pto` | Upcoming PTO for a person (holidays + scheduled time off + time-off cards) |
| `list_holidays` | Company holiday calendars by location |
| `list_engagements` | Engagements with managers and nested projects |
| `get_engagement` | One engagement with contracts, cost center and projects |
| `list_project_roles` | Who is assigned to one or more projects |
| `list_proj_bookings` | Booked hours on one or more projects in a date window |

The server also publishes MCP **prompts** (recipes such as `projector_availability`, `projector_project_bookings`) and **resources** (`projector://resources/{id}`, reference catalogs).

## How it works

```
MCP client ──Bearer JWT──▶ this server ──Projector session ticket──▶ Projector PSA (SOAP)
    │                          │
    └──OAuth 2.1 + PKCE────────┘  (the server is the OAuth authorization server for MCP clients
                                   and a client of Projector OAuth behind the scenes)
```

Projector OAuth returns an opaque session ticket, not a JWT. The server keeps that ticket on the server side (encrypted in Azure SQL) and gives the MCP client its own short-lived JWT. MCP clients register themselves automatically (Dynamic Client Registration), so users only paste the server URL.

Optionally, the server can require a Microsoft Entra ID sign-in before the Projector sign-in, to restrict access to your tenant.

## Get started

1. **Register an OAuth app in Projector** and give users the right permissions → [docs/projector-oauth-setup.md](docs/projector-oauth-setup.md)
2. **Deploy to Azure** → [docs/deploy-azure.md](docs/deploy-azure.md)
3. **Connect your client:**
   - [VS Code](docs/clients/vscode.md)
   - [Cursor](docs/clients/cursor.md)
   - [Claude](docs/clients/claude.md) (Claude.ai, Claude Desktop, Claude Code)

Developing or testing locally? See [docs/local-dev.md](docs/local-dev.md).

## Repository layout

| Path | Contents |
|------|----------|
| `src/Projector.Mcp.Server` | ASP.NET Core host: MCP endpoint, OAuth broker, CLI, prompts, resources |
| `src/Projector.Application` | Tool logic, token stores (in-memory, DPAPI, Azure SQL) |
| `src/Projector.ApiClient` | Projector SOAP and OAuth clients |
| `src/Projector.Domain`, `src/Projector.Contracts` | Domain model and tool DTOs |
| `tests/Projector.UnitTests` | Offline tests (XML fixtures, protocol, catalog) and opt-in live tests |
| `infrastructure/` | Bicep template and PowerShell deploy scripts |

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Projector.Mcp.sln
dotnet test tests/Projector.UnitTests --filter "Category!=Live"
```

## Security

See [SECURITY.md](SECURITY.md). Never commit secrets, session tickets or refresh tokens.

## License

[MIT](LICENSE)
