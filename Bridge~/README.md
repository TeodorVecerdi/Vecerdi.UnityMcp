# Unity MCP Server

A Model Context Protocol (MCP) server that enables AI agents to interact with the Unity Editor. This allows AI coding assistants to verify their work, see compilation errors, run tests, and control play mode.

## Architecture

```
┌─────────────────────┐
│   AI Agent          │
│   (Claude, etc.)    │
└─────────────────────┘
         │ MCP Protocol (stdio)
┌─────────────────────┐
│  unity-mcp          │  ← This tool (C# console app, "the bridge")
│  (MCP Server)       │
└─────────────────────┘
         │ WebSocket (one pooled connection per editor)
┌─────────────────────┐
│  Unity Editor(s)    │
│  └─ McpEditorServer │  ← Editor plugin (auto-starts)
└─────────────────────┘
```

Tools come from two places:

- **Native tools** live in this bridge (`UnityTools.cs`): everything with bridge-side logic —
  `sync_and_compile`'s wait/diagnostics orchestration, `run_tests` polling, `get_logs`
  formatting, `invoke_managed_method`, and the editor-discovery pair
  (`list_editors`/`select_editor`).
- **Dynamic tools** are advertised by the editor itself: Unity-side commands implementing
  `IMcpToolProvider` are enumerated via `unity.meta.listTools` and registered by
  `DynamicToolManager` whenever an editor connection (re)opens — including the reconnect after
  every domain reload, so a `sync_and_compile` that compiles a new tool makes it appear in the
  same agent session (`notifications/tools/list_changed`). **Adding an editor-side tool
  therefore needs no bridge rebuild**: implement the command + descriptor in
  the editor plugin (`Vecerdi.UnityMcp/` under your project's `Assets/`), register it in `McpEditorServer`, recompile.
  Native tool names shadow dynamic ones, so bridge tools can be migrated gradually.

## Prerequisites

1. **.NET 10 SDK** installed
2. **Unity Editor** with the MCP plugin (the `Vecerdi.UnityMcp/` folder under your project's `Assets/`)

## Layout

The bridge lives in `Bridge~/` inside the editor plugin folder. The trailing `~` hides it from Unity's
importer, so the plugin and the bridge share one folder (and one repository) without Unity generating
meta files or trying to compile the bridge sources.

```
Vecerdi.UnityMcp/               # editor plugin (compiled by Unity)
└── Bridge~/                    # this bridge (ignored by Unity)
    ├── UnityMcp.slnx           # solution: bridge + tests
    ├── Directory.Build.props   # shared settings + output location
    ├── src/UnityMcp/           # the MCP server console app
    └── tests/UnityMcp.Tests/   # xunit tests
```

## Building

```bash
cd Bridge~
dotnet build
```

For MCP clients, publish a stable executable and point the client at that binary. Build output uses the
artifacts output layout. A hosting repository that defines `ArtifactsPath` in a `Directory.Build.props`
above this folder decides where that is (MediaVault routes it to `<repo>/artifacts/`); otherwise output
goes to `Bridge~/artifacts/`. Either way the published binary lands at
`<artifacts>/publish/UnityMcp/release/unity-mcp.exe`:

```bash
dotnet publish src/UnityMcp/UnityMcp.csproj -c Release
```

If you change code under `Bridge~/src`, rerun that publish command before expecting MCP clients to use the updated implementation. If publish fails because `unity-mcp.exe` or `unity-mcp.dll` is locked, stop the MCP client that is currently running the server, publish again, then restart the client. (Editor-side tool changes need neither publish nor client restart — see dynamic tools above.)

## Configuration for AI Agents

```json
{
  "mcpServers": {
    "unity": {
      "command": "<repo>/artifacts/publish/UnityMcp/release/unity-mcp.exe"
    }
  }
}
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `UNITY_MCP_LOG_LEVEL` | `information` | Bridge log verbosity: `trace`, `debug`, `information`, `warning`, `error` |

### Logging

The bridge logs **only to stderr** (stdout carries the MCP protocol); there is no log file.
Your MCP client captures stderr — Claude Code writes it (plus its own tool-call tracing) to
per-launch JSONL files under
`%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project-slug>\mcp-logs-unity-mcp\`.

## Available Tools

Native (bridge-side):

| Tool | Description |
|------|-------------|
| `sync_and_compile` | **Default "make my edits take effect" call.** Refresh + recompile + wait + fresh diagnostics, as one operation |
| `refresh_assets` | Refresh asset database; reports whether a compile was triggered (prefer `sync_and_compile` for code changes) |
| `get_logs` | Get recent Unity console logs (with timestamps + buffer-wrap note) |
| `invoke_managed_method` | Invoke managed methods via reflection with JSON arguments; async results (Task, ValueTask, UniTask, Awaitable) are awaited up to `waitMs`, then backgrounded (see below) |
| `run_tests` | Run Unity tests with filter support and optional wait-for-completion |
| `get_test_run_status` | Get status/results of a test run |
| `cancel_test_run` | Cancel an active test run |
| `list_editors` / `select_editor` | Discover editors and set the default target |

Dynamic (editor-advertised; the live set is whatever the connected editor exposes):

| Tool | Description |
|------|-------------|
| `get_play_mode_state` / `set_play_mode` | Query / enter / exit play mode |
| `execute_menu_item` | Execute a menu item by path |
| `clear_logs` | Clear the log buffer |
| `get_invocation_result` | Poll a backgrounded `invoke_managed_method` call |

### Key semantics

- **Making edits take effect:** call `sync_and_compile`. It drains any in-flight
  import/compile, refreshes assets, forces a recompile, waits out the domain
  reload, and returns **only** the compiler diagnostics from that compile (parsed
  into `file(line,col): severity CODE: message`). Do **not** chain
  `refresh_assets` then a manual recompile - that race is the footgun this tool removes.
- **Domain-reload contract:** `sync_and_compile` blocks (up to ~3 min)
  and drives a domain reload. The editor keeps its discovery entry through the reload,
  flagged `reloading`, so any *other* call that arrives mid-reload waits for the editor to
  come back (up to a minute) and then sends, instead of failing with a connect error. A
  request that was already in flight when the socket dropped fails with a message naming
  the reload; treat it as "unknown whether it ran" for non-idempotent commands.
- **Joining a compile you did not start:** if the editor is already compiling when
  `sync_and_compile` arrives, or starts compiling while the tool is still draining (you
  focused the editor a moment ago, say), the tool rides
  that compile instead of forcing a second one: it takes the compile's own start time as
  the diagnostics marker, waits for it to settle, refreshes the asset database, and only
  compiles again if the refresh found newer edits. The result says which happened.
- **Auto-connect:** a tool call with no `port` attaches to the `select_editor`
  default, or to the only running editor when exactly one exists.
- **Per-editor state:** the default target and the "latest" test run are tracked
  per editor, and the latest run is undefined across a domain reload - pass the
  `runId` from `run_tests` to be safe.
- **Main-thread execution:** the editor processes ~10 queued commands per frame
  on the main thread, so a slow `invoke_managed_method` stalls other queued calls
  to that editor. Calls are serialized, not parallel.
- **Async invokes never deadlock:** `invoke_managed_method` waits at most `waitMs`
  (default 2s) for an async result - `Task`, `ValueTask`, `UniTask` or Unity's
  `Awaitable`, generic or not - then returns `{pending: true, invocationId}`
  and lets the main thread resume - which is what allows main-thread-bound
  continuations (UniTask/PlayerLoop, Awaitable, the Unity sync context) to complete. Poll `get_invocation_result` for
  the outcome (handed out once; entries expire after ~1h or on domain reload).
- **run_tests filters:** `testNames` is prefix/class matching - a class FQN runs
  all its methods. The result labels *matched-and-ran* separately from the
  whole-tree *discovered* count and echoes the resolved filter, so "5 passed"
  can't be mistaken for the wrong 5.

## Unity Editor Plugin

The bridge communicates with Unity via a WebSocket server running in the Editor. The plugin:

- Auto-starts when Unity loads (`[InitializeOnLoad]`)
- Allocates a port dynamically from 9100 upward and registers it in a discovery file, so
  multiple editors can run side by side (`list_editors` reads that registry)
- Advertises its lifecycle in that entry - `ready`, `compiling` (with the compile's start
  time) or `reloading` - and only removes the entry when the editor quits, so the bridge
  can tell a domain reload from a closed editor
- Does not start inside Unity's asset import worker processes, which load the same editor
  assemblies but must not register as editors
- Processes commands on the main thread (safe for Unity API calls)
- Survives play mode transitions and script reloads (re-registering on its previous port)

### Plugin Location

```
<project>/Assets/.../Vecerdi.UnityMcp/
├── Bridge~/                  # the bridge (this README), ignored by Unity
├── McpEditorServer.cs        # WebSocket server + command registration
├── McpServerWindow.cs        # Editor window (Window > Unity MCP Server)
├── EditorInstanceRegistry.cs # Dynamic port allocation + discovery file
├── Protocol/
│   └── McpMessage.cs         # Request/Response types
└── Commands/
    ├── McpCommandRegistry.cs # Dispatch + IMcpToolProvider/McpToolDescriptor
    ├── MetaCommands.cs       # unity.meta.listTools (dynamic tool discovery)
    ├── DebugCommands.cs      # getLogs, clearLogs
    ├── EditorCommands.cs     # recompile, playMode, menu items, editor info
    └── ManagedInvocationCommands.cs # reflection invoke + pending-invocation registry
```

## Troubleshooting

### "Failed to connect to Unity Editor" / "No Unity Editor instances found"

1. Make sure Unity Editor is running
2. Check `Window > Unity MCP Server` - the server should show "Running"
3. Call `list_editors` to see discovered instances; right after Unity starts, the first
   call can race discovery - retry once
4. If a modal dialog is open in the editor, every call fails generically while
   `list_editors` still works - dismiss the dialog
5. "has been reloading the script domain for Ns" means the entry is stuck in `reloading`:
   the editor never came back from a domain reload (a dialog, a crash, or a very long
   import). Check the editor window; `list_editors` shows the state and how long it has
   been in it

### No logs appearing

1. The log buffer only captures logs while the plugin is active
2. Logs from before the server started won't be available
3. Try triggering an action that generates logs, then call `get_logs`

### Compilation takes a long time

If a compile appears stuck:
1. Check Unity console for errors preventing compilation
2. Use `sync_and_compile` (it already folds in the asset refresh and waits out
   the reload) - do not chain `refresh_assets` then a manual recompile

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
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_logs","arguments":{"count":10}}}
```
