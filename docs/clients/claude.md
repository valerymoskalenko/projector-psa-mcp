# Use with Claude

You need the MCP server URL of your deployment: `https://<your-web-app>.azurewebsites.net/mcp`.

The server supports Dynamic Client Registration, so you don't need an OAuth client ID. Leave **Advanced settings** empty.

## Claude.ai, Claude Desktop and Claude mobile

Connectors are shared across these apps: add the connector once, then use it everywhere you sign in with the same account.

**Pro / Max:**

1. **Customize → Connectors** → **+** → **Add custom connector**.
2. Name: `Projector PSA`. URL: your MCP server URL.
3. **Add**, then **Connect**. A browser window opens: sign in with Microsoft (if your deployment uses the Entra step), then with Projector, and approve.

**Team / Enterprise:** an owner adds the connector once in **Organization settings → Connectors → Add → Custom → Web** (same URL). Then each member opens **Customize → Connectors**, finds **Projector PSA** and clicks **Connect**.

Claude calls the server from Anthropic's cloud (outbound range `160.79.104.0/21`). The App Service must be reachable from the internet, which is the default.

## Claude Code

```bash
claude mcp add --transport http projector-psa https://<your-web-app>.azurewebsites.net/mcp
```

Then run `/mcp` inside Claude Code, pick **projector-psa** and authenticate. Claude Code uses a loopback redirect (`http://localhost:<port>/callback`), which the server always allows.

Add `--scope project` to share the server with your team through `.mcp.json`, or `--scope user` to use it in all your projects.

## Try it

- "What is Jane Doe's availability for the next three weeks? Can she take 30 hours per week?"
- "Who is booked on project P001234-001 in October?"
- "List my timecards for last month."

## Troubleshooting

| Symptom | Check |
|---------|-------|
| "Couldn't reach the MCP server" | `https://<app>/health` returns 200; `/.well-known/oauth-protected-resource` returns JSON |
| `redirect_uri is not allowlisted` | The callback in the error must be in the server's allowlist (see [deploy-azure.md](../deploy-azure.md#allowed-redirect-uris)) |
| Tool calls fail with a permission error | The user needs **Web Services Access** in Projector ([projector-oauth-setup.md](../projector-oauth-setup.md#2-user-permissions)) |
| Asked to sign in again | Normal after the Projector session ends, or after the Projector OAuth secret was rotated |

Claude's documentation: [Custom connectors using remote MCP](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp).
