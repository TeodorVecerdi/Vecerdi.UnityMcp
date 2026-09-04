# Unity MCP Agent Notes

This directory inherits the repository-wide instructions from the consuming repository's root `AGENTS.md`.

## Copilot CLI / MCP launch workflow

- The consuming repository's `.mcp.json` (and any other MCP client config) is expected to launch the published binary at `artifacts\publish\UnityMcp\release\unity-mcp.exe` (the artifacts output layout; the hosting repository's `Directory.Build.props` decides the artifacts root, else it is `Bridge~\artifacts\` - see `Directory.Build.props` here).
- Do **not** switch the MCP entry back to `dotnet run` unless the user explicitly asks for that behavior.

## After changing code in this directory

If you change files under `Bridge~\src\`, republish the MCP server before considering the change complete:

```powershell
dotnet publish <path-to>\Bridge~\src\UnityMcp\UnityMcp.csproj -c Release -nologo
```

## Common publish failure

If publish fails because `unity-mcp.exe` or `unity-mcp.dll` is locked, the running MCP client usually has the published server loaded already. In that case:

1. Stop Copilot CLI or any other MCP client currently using `unity-mcp`.
2. Run the publish command again.
3. Restart the client so it picks up the new binary.
