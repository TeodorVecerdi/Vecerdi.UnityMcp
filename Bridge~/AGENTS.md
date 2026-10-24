# Unity MCP Agent Notes

This directory inherits the repository-wide instructions from the consuming repository's root `AGENTS.md`.

## Copilot CLI / MCP launch workflow

- The consuming repository's `.mcp.json` (and any other MCP client config) is expected to launch the published binary at `artifacts\publish\UnityMcp\release\unity-mcp.exe` (the artifacts output layout; the hosting repository's `Directory.Build.props` decides the artifacts root, else it is `Bridge~\artifacts\` - see `Directory.Build.props` here).
- Do **not** switch the MCP entry back to `dotnet run` unless the user explicitly asks for that behavior.

## After changing code in this directory

If you change files under `Bridge~/src/`, republish the MCP server before considering the change complete:

```powershell
<path-to>/Bridge~/publish.ps1
```

It is safe to run while MCP clients (including the one you are running in) have the server loaded: changed files
that are in use are set aside as `*.old` instead of failing the publish. Running clients keep the previous build
until they restart, and the script lists them by PID. Tell the user a client restart is needed to pick the change up.
