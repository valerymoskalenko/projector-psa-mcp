# Use with VS Code (GitHub Copilot)

VS Code connects to the deployed server over HTTP and handles the OAuth sign-in itself.

## Add the server

Run **MCP: Add Server…** from the Command Palette → **HTTP** → paste `https://<your-web-app>.azurewebsites.net/mcp` → name it `projector-psa`. Choose **Workspace** to share it through the repository, or **Global** for all workspaces.

Or edit the file directly: `.vscode/mcp.json` in a workspace, or **MCP: Open User Configuration** for all workspaces:

```json
{
  "servers": {
    "projector-psa": {
      "type": "http",
      "url": "https://<your-web-app>.azurewebsites.net/mcp"
    }
  }
}
```

## Sign in

Start the server (the **Start** code lens in `mcp.json`, or **MCP: List Servers → projector-psa → Start Server**). VS Code asks to authenticate and opens the browser: sign in with Microsoft (if your deployment uses the Entra step), then with Projector.

No client ID is needed: VS Code registers itself (Dynamic Client Registration) and uses a loopback or `https://vscode.dev/redirect` callback, both of which the server allows.

## Use it

Open Copilot Chat in **Agent** mode. The Projector tools appear under the tools picker. Ask, for example, "Check availability for Jane Doe and John Smith for the next two weeks at 20 hours per week."

To sign out or switch accounts: **Accounts** menu → the Projector entry → **Sign Out**, or **MCP: List Servers → projector-psa → Disconnect Account**.

VS Code documentation: [MCP servers in VS Code](https://code.visualstudio.com/docs/copilot/customization/mcp-servers).
