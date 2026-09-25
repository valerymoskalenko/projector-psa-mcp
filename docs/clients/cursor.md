# Use with Cursor

## Add the server

Edit `~/.cursor/mcp.json` (all projects) or `.cursor/mcp.json` (this project):

```json
{
  "mcpServers": {
    "projector-psa": {
      "url": "https://<your-web-app>.azurewebsites.net/mcp"
    }
  }
}
```

Or use **Cursor Settings → MCP → Add new MCP server** with the same URL.

## Sign in

In **Cursor Settings → MCP**, the server shows **Needs authentication** (or a **Connect** button). Click it; the browser opens: sign in with Microsoft (if your deployment uses the Entra step), then with Projector. Cursor registers itself automatically, so no client ID is needed.

## Use it

In Agent chat, ask, for example, "What PTO does Jane Doe have coming up this quarter?" Cursor asks before running each tool unless you allow it.

Cursor documentation: [Model Context Protocol](https://cursor.com/docs/context/mcp).
