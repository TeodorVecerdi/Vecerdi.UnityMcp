# Unity MCP Server

A Model Context Protocol (MCP) server that enables AI agents to interact with the Unity Editor. This allows AI coding assistants to verify their work, see compilation errors, and control play mode.

## Architecture

```
┌─────────────────────┐
│   AI Agent          │
│   (Claude, etc.)    │
└─────────────────────┘
         │ MCP Protocol (stdio)
┌─────────────────────┐
│  unity-mcp          │  ← This tool (C# console app)
│  (MCP Server)       │
└─────────────────────┘
         │ WebSocket
┌─────────────────────┐
│  Unity Editor       │
│  └─ McpEditorServer │  ← Editor plugin (auto-starts)
└─────────────────────┘
```

## Prerequisites

1. **.NET 10 SDK** installed
2. **Unity Editor** with the MCP plugin (in `MediaVault/Assets/Scripts/UnityMcp.Editor/`)

## Building

```bash
cd tools/unity-mcp
dotnet build
```

For Copilot CLI and other MCP clients, prefer publishing a stable executable and pointing the client at that binary. Build output uses the artifacts output layout (see the repo-root `tools/Directory.Build.props`), so the published binary lands at `<repo>/artifacts/publish/UnityMcp/release/unity-mcp.exe`:

```bash
dotnet publish -c Release
```

If you change code under `tools/unity-mcp`, rerun that publish command before expecting MCP clients to use the updated implementation.

If publish fails because `unity-mcp.exe` or `unity-mcp.dll` is locked, stop the MCP client that is currently running the server, publish again, then restart the client.

## Configuration for AI Agents

### Claude Desktop / Cursor / etc.

For local manual development you can use `dotnet run`, but for normal MCP client configuration prefer the published executable to avoid restore/build-on-start delays and file-locking issues.

Published executable (recommended):

```json
{
  "mcpServers": {
    "unity": {
      "command": "D:/dev/unity/media-vault/artifacts/publish/UnityMcp/release/unity-mcp.exe",
      "env": {
        "UNITY_MCP_LOG": "D:/dev/unity/media-vault/tools/unity-mcp/mcp.log"
      }
    }
  }
}
```

`dotnet run` example for ad-hoc local use:

```json
{
  "mcpServers": {
    "unity": {
      "command": "dotnet",
      "args": ["run", "--project", "D:/dev/unity/media-vault/tools/unity-mcp/UnityMcp.csproj"],
      "env": {
        "UNITY_MCP_LOG": "D:/dev/unity/media-vault/tools/unity-mcp/mcp.log"
      }
    }
  }
}
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `UNITY_MCP_URI` | `ws://localhost:9999/` | WebSocket URI of the Unity MCP plugin |
| `UNITY_MCP_LOG` | (none) | Path to log file for debugging |

## Available Tools

| Tool | Description |
|------|-------------|
| `sync_and_compile` | **Default "make my edits take effect" call.** Refresh + recompile + wait + fresh diagnostics, as one operation |
| `get_logs` | Get recent Unity console logs (with timestamps + buffer-wrap note) |
| `clear_logs` | Clear the log buffer |
| `recompile` | Force script recompilation (equivalent to `sync_and_compile`; kept for compatibility) |
| `get_play_mode_state` | Check play mode state |
| `set_play_mode` | Set play mode state (enter/exit via `isPlaying`) |
| `refresh_assets` | Refresh asset database; reports whether a compile was triggered |
| `invoke_managed_method` | Invoke managed methods via reflection with JSON arguments |
| `run_tests` | Run Unity tests with filter support and optional wait-for-completion |
| `get_test_run_status` | Get status/results of a test run |
| `cancel_test_run` | Cancel an active test run |
| `list_editors` / `select_editor` | Discover editors and set the default target |

### Key semantics

- **Making edits take effect:** call `sync_and_compile`. It drains any in-flight
  import/compile, refreshes assets, forces a recompile, waits out the domain
  reload, and returns **only** the compiler diagnostics from that compile (parsed
  into `file(line,col): severity CODE: message`). Do **not** chain
  `refresh_assets` then `recompile` - that race is the footgun this tool removes.
- **Domain-reload contract:** `sync_and_compile`/`recompile` block (up to ~3 min)
  and drive a domain reload; any *other* call to the same editor mid-reload fails
  with a connect error. Expect it, and retry after the call returns.
- **Auto-connect:** a tool call with no `port` attaches to the `select_editor`
  default, or to the only running editor when exactly one exists.
- **Per-editor state:** the default target and the "latest" test run are tracked
  per editor, and the latest run is undefined across a domain reload - pass the
  `runId` from `run_tests` to be safe.
- **Main-thread execution:** the editor processes ~10 queued commands per frame
  on the main thread, so a slow `invoke_managed_method` stalls other queued calls
  to that editor. Calls are serialized, not parallel.
- **run_tests filters:** `testNames` is prefix/class matching - a class FQN runs
  all its methods. The result labels *matched-and-ran* separately from the
  whole-tree *discovered* count and echoes the resolved filter, so "5 passed"
  can't be mistaken for the wrong 5.

## Tool Parameters

### `unity_get_logs`

```json
{
  "count": 100,        // Max entries to return
  "minLevel": "error", // Filter: "info", "warning", or "error"
  "filter": "NullRef"  // Text filter (case-insensitive)
}
```

### `unity_invoke_managed_method`

```json
{
  "typeName": "UnityEditor.EditorApplication",
  "methodName": "ExecuteMenuItem",
  "arguments": ["File/Save Project"],
  "parameterTypeNames": ["System.String"]
}
```

### `unity_run_tests`

```json
{
  "testMode": "EditMode",
  "assemblyNames": ["MediaVault.Tests"],
  "categoryNames": ["Fast"],
  "waitForCompletion": true
}
```

## Example Usage

Once configured, an AI agent can:

1. **Check for errors after making code changes:**
   - Call `sync_and_compile` - it refreshes, recompiles, waits, and returns only
     the fresh compiler diagnostics in one call (no separate `get_logs` needed)

2. **Test runtime behavior:**
   - Call `unity_set_play_mode` with `isPlaying: true`
   - Wait for initialization
   - Call `unity_get_logs` to see runtime errors
   - Call `unity_set_play_mode` with `isPlaying: false`

3. **Debug issues:**
   - Call `unity_get_logs` with a `filter` for the relevant component name
   - Check stack traces in the response

## Unity Editor Plugin

The server communicates with Unity via a WebSocket server running in the Editor. The plugin:

- Auto-starts when Unity loads (`[InitializeOnLoad]`)
- Listens on `ws://localhost:9999/`
- Processes commands on the main thread (safe for Unity API calls)
- Survives play mode transitions and script reloads

### Plugin Location

```
MediaVault/Assets/Scripts/UnityMcp.Editor/
├── McpEditorServer.cs       # WebSocket server
├── McpServerWindow.cs       # Editor window (Window > Unity MCP Server)
├── Protocol/
│   └── McpMessage.cs        # Request/Response types
└── Commands/
    ├── McpCommandRegistry.cs
    ├── DebugCommands.cs     # getLogs, clearLogs
    └── EditorCommands.cs    # recompile, playMode, etc.
```

## Troubleshooting

### "Failed to connect to Unity Editor"

1. Make sure Unity Editor is running
2. Check `Window > Unity MCP Server` - the server should show "Running"
3. Verify the port matches `UNITY_MCP_URI` (default: 9999)

### No logs appearing

1. The log buffer only captures logs while the plugin is active
2. Logs from before the server started won't be available
3. Try triggering an action that generates logs, then call `unity_get_logs`

### Compilation takes a long time

If recompile appears stuck:
1. Check Unity console for errors preventing compilation
2. Use `sync_and_compile` (it already folds in the asset refresh and waits out
   the reload) - do not chain `refresh_assets` then `recompile` manually

## Development

To test the MCP server manually:

```bash
# Start the server
dotnet run

# Send a request (paste this as a single line):
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}

# List tools:
{"jsonrpc":"2.0","id":2,"method":"tools/list"}

# Call a tool:
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"unity_get_logs","arguments":{"count":10}}}
```
