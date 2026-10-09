# Local development

For contributors and for testing changes before deploying. End users should connect to the Azure deployment instead ([clients](../README.md#get-started)).

> **Windows only.** The local session cache is protected with Windows DPAPI (`%LOCALAPPDATA%\ProjectorMcp\oauth-sessions`).

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Projector OAuth app with the redirect URL `http://localhost:5180/oauth/projector/callback` ([projector-oauth-setup.md](projector-oauth-setup.md))

## 2. Configure

Use [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) so nothing lands in the repository:

```powershell
$p = 'src/Projector.Mcp.Server'
dotnet user-secrets set Projector:AccountCode  "<your account code>" --project $p
dotnet user-secrets set Projector:ClientId     "<dev client id>"     --project $p
dotnet user-secrets set Projector:ClientSecret "<dev client secret>" --project $p
```

Alternatively, keep the dev client in Key Vault: set `Projector:KeyVaultUri` instead of the client ID and secret. The secrets must be named `ProjectorPSADevOauthClientID` and `ProjectorPSADevOauthSecret` (see `appsettings.Development.json`). Your `az login` identity needs read access to them.

Set the environment once per terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
```

## 3. Sign in and run tools from the CLI

```powershell
$p = 'src/Projector.Mcp.Server'
dotnet run --project $p --no-launch-profile -- auth login          # browser sign-in, caches the session
dotnet run --project $p --no-launch-profile -- tool get_resource --full-name "Jane Doe"
dotnet run --project $p --no-launch-profile -- tool list_upcoming_pto --resource-id 10001 --start-date 2026-09-01 --end-date 2026-12-30
dotnet run --project $p --no-launch-profile -- auth logout         # revoke and clear the cache
```

Tool arguments are the tool's parameters in `--kebab-case` (see `src/Projector.Mcp.Server/tools.json`).

## 4. Run as an MCP server

**stdio** (uses the session from `auth login`). The repository includes ready-made configs: `.vscode/mcp.json` (VS Code), `.cursor/mcp.json` (Cursor), `.mcp.json` (Claude Code). They run:

```text
dotnet run --project src/Projector.Mcp.Server/Projector.Mcp.Server.csproj --no-launch-profile --verbosity quiet -- serve --stdio
```

**HTTP** on `http://127.0.0.1:5180/mcp`, with the full OAuth flow (no Entra step locally; in-memory token store):

```powershell
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- serve --http
```

Point [MCP Inspector](https://github.com/modelcontextprotocol/inspector) or any client at `http://localhost:5180/mcp`.

## 5. Tests

```powershell
dotnet test tests/Projector.UnitTests --filter "Category!=Live"   # offline: fixtures, protocol, catalog
```

Live tests call your real Projector account (read-only), using the session from `auth login`. They need a few environment variables that describe your data:

```powershell
$env:PROJECTOR_LIVE_ACCOUNT_CODE = '<account code>'
$env:PROJECTOR_LIVE_RESOURCES    = '10001:jane.doe@contoso.com;10002'   # first = primary resource
$env:PROJECTOR_LIVE_PROJECT_CODE = 'P001234-001'
dotnet test tests/Projector.UnitTests --filter "Category=Live"
```

Optional: `PROJECTOR_LIVE_RESOURCE_SEARCH`, `PROJECTOR_LIVE_PTO_COUNT`, `PROJECTOR_LIVE_KEYVAULT_URI`. See `LiveSettings` in `tests/Projector.UnitTests/LiveCachedToolTests.cs`.

## Build locks

If a local MCP session (stdio) is running from your editor, `dotnet build` can fail with "file is locked by Projector.Mcp.Server". Stop the MCP server in the editor, or build to the git-ignored `artifacts` folder inside the repository: `dotnet test tests/Projector.UnitTests --artifacts-path artifacts --filter "Category!=Live"`. Keep the folder inside the repository: the tests find `fixtures/` by walking up from the test binary.

## Check a raw Projector response (`pws`, dev only)

Before adding a field or flag, look at what Projector actually returns. `pws` posts a request body you write, signed in with your local `auth login` session, and prints the response XML:

```powershell
# body.xml: <pws:PwsGetTimeEntryParameters><pws:serviceRequest><req:SessionTicket>{{ticket}}</req:SessionTicket></pws:serviceRequest></pws:PwsGetTimeEntryParameters>
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- pws PwsGetTimeEntryParameters body.xml
```

- `{{ticket}}` is replaced by your session ticket. Prefixes `pws:`, `req:`, `com:`, `tim:` and `sch:` are declared for you.
- Read-only by default: only `PwsGet…` and `PwsSearch…` methods are sent, and the body element must match the method. Saves, deletes, submits and approvals are refused before any call.
- `--write` also allows `PwsSaveExpenseDocument`, `PwsDeleteExpenseDocument` and `PwsDeleteDocument`, sent once and never retried, for checking expense request shapes on a test draft report. Nothing else that changes data is ever sent. Delete the test report afterwards: `PwsDeleteExpenseDocument` returns its receipts to the pool, `PwsDeleteDocument` (`DeletePermanentlyFlag`) removes them.
- Runs only with `ASPNETCORE_ENVIRONMENT=Development`.
- It runs as you, with your Projector permissions. Don't commit captured responses: turn them into fixtures with invented names and ids.

Receipt files are not SOAP. `upload-receipt` posts one file into a document folder (for receipts: the folder UID of `PwsGetFolder` with `UserReceiptPoolFolder` and your `UserIdentity`) and prints Projector's JSON answer with the `DocumentRefUid`:

```powershell
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- upload-receipt receipt.pdf <folder-uid>
```

`save_expenses` from the command line takes the cards as a file. For local tests a receipt may name a `file_path` (relative to the JSON file) instead of `content_base64`:

```powershell
# cards.json: [{"date":"2026-10-02","project_code":"C000001-003","expense_type":"Office Fee","description":"Test","amount":1,"receipt":{"file_path":"receipt.png"}}]
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- tool save_expenses --cards-json cards.json --report-name "Test - delete me"
```

The write commands (`save_timecard`, `save_expenses`, `save_booking`) only check by default, like the MCP tools; add `--dry-run false` to save.

On the HTTP server, a receipt file can also be uploaded the way an AI client does it: `list_expenses` with `include_options` returns `options.receipt_upload` (URL and a 30-minute ticket), then

```powershell
curl.exe -sS -F ticket=<ticket> -F 'file=@"Sep08 - Taxi 55,67CAD.pdf"' http://localhost:5180/receipts/upload
```

answers with the `receipt_uid` that `save_expenses` takes in `receipt.receipt_uid`. Keep the double quotes around the path inside the `-F` value: curl reads a comma or semicolon after `file=@` as the start of an option, so an unquoted name like the one above fails with curl error 26.

To change one field of an existing card, send `card_uid` and only that field (the other fields and the card's receipts stay), e.g. `[{"card_uid":"4000…","description":"Taxi to the hotel"}]` with `--report ER0…`. `--brief true` keeps the answer of a real save to the cards with warnings or errors. The stdio server and the `tool` command have no upload endpoint and return no `receipt_upload`.

The legacy (ASMX) report and export methods behind `get_report` have their own command. The file holds the parameter elements and may be empty:

```powershell
# params.xml: <data:MaxRowsToReturn>10</data:MaxRowsToReturn><data:OnlyCountRows>false</data:OnlyCountRows>
dotnet run --project src/Projector.Mcp.Server --no-launch-profile -- asmx ExportProjectList params.xml
```

- Sent without a switch: `GetReportStatus`, `ExportProjectList`, `ExportTimeCards`, `ExportOlapGinsuRecords`, `ExportResources`, `ExportScheduledTimeoff`.
- `SubmitReportSpec` and `SubmitOlapGinsuExport` start a run in Projector (no business data changes) and are sent only with `--run`. Every other method is refused.
