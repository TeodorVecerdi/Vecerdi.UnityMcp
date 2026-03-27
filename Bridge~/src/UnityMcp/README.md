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

Or publish a self-contained executable:

```bash
dotnet publish -c Release -o ./publish
```

## Configuration for AI Agents

### Claude Desktop / Cursor / etc.

Add to your MCP configuration (e.g., `claude_desktop_config.json`):

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

Or if you've published it:

```json
{
  "mcpServers": {
    "unity": {
      "command": "D:/dev/unity/media-vault/tools/unity-mcp/publish/unity-mcp.exe",
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
| `unity_get_logs` | Get recent Unity console logs (errors, warnings, info) |
| `unity_clear_logs` | Clear the log buffer |
| `unity_recompile` | Force script recompilation |
| `unity_get_play_mode_state` | Check play mode state |
| `unity_set_play_mode` | Set play mode state (enter/exit via `isPlaying`) |
| `unity_refresh_assets` | Refresh asset database |
| `unity_invoke_managed_method` | Invoke managed methods via reflection with JSON arguments |
| `unity_run_tests` | Run Unity tests with filter support and optional wait-for-completion |
| `unity_get_test_run_status` | Get status/results of a test run |
| `unity_cancel_test_run` | Cancel an active test run |

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
   - Call `unity_recompile`
   - Call `unity_get_logs` with `minLevel: "error"` to see any compilation errors

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
2. Try `unity_refresh_assets`, then run `unity_recompile` again

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
