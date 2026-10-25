# Unity MCP bridge

The MCP server half of [Vecerdi.UnityMcp](../README.md): a .NET console app that speaks MCP over stdio
to the agent and forwards calls to the editor plugin. This file covers how it is built and how it fits
together. Installing, publishing, connecting a client, and the tool reference are in the
[repository README](../README.md).

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
  `sync_and_compile`'s wait/diagnostics orchestration, `run_tests` polling,
  `invoke_managed_method`, and the editor-discovery pair (`list_editors`/`select_editor`).
- **Dynamic tools** are advertised by the editor itself: Unity-side commands implementing
  `IMcpToolProvider` are enumerated via `unity.bridge.listTools` and registered by
  `DynamicToolManager` whenever an editor connection (re)opens — including the reconnect after
  every domain reload, so a `sync_and_compile` that compiles a new tool makes it appear in the
  same agent session (`notifications/tools/list_changed`). **Adding an editor-side tool
  therefore needs no bridge rebuild**: implement the command + descriptor in
  the editor plugin (`Vecerdi.UnityMcp/` under your project's `Assets/`), register it in `McpEditorServer`, recompile.
  Native tool names shadow dynamic ones, so bridge tools can be migrated gradually.

Editor commands follow the same split. The ones the bridge calls by name live under `unity.bridge.*`
(`Bridge*Command` on the Unity side): `listTools`, the compile and refresh pair, the structured
`getLogs` that `sync_and_compile` reads diagnostics from, `invokeManagedMethod`, and `runTests` /
`getTestRunStatus`. They are the protocol between the two halves, so renaming or reshaping one
breaks every published bridge that uses it. Commands that back a dynamic tool are reached through
`listTools`, so their names are free to change.

## Layout

The bridge lives in `Bridge~/` inside the editor plugin folder. The trailing `~` hides it from Unity's
importer, so the plugin and the bridge share one folder (and one repository) without Unity generating
meta files or trying to compile the bridge sources.

```
Vecerdi.UnityMcp/               # editor plugin (compiled by Unity)
└── Bridge~/                    # this bridge (ignored by Unity)
    ├── UnityMcp.slnx           # solution: bridge + tests
    ├── Directory.Build.props   # shared settings + output location
    ├── publish.ps1             # publish to the stable location, safe while clients run it
    ├── src/UnityMcp/           # the MCP server console app
    └── tests/UnityMcp.Tests/   # xunit tests
```

## Building and testing

```bash
cd Bridge~
dotnet build
dotnet test
```

Build output uses the artifacts output layout. A hosting repository that defines `ArtifactsPath` in a
`Directory.Build.props` above this folder decides where that is (MediaVault routes it to
`<repo>/artifacts/`); otherwise output goes to `Bridge~/artifacts/`.

MCP clients run the *published* binary, so a change under `src/` reaches them only after
`./publish.ps1` and a client restart (see
[Publishing the bridge](../README.md#publishing-the-bridge)).

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
    ├── MetaCommands.cs       # unity.bridge.listTools (dynamic tool discovery)
    ├── EditorCommands.cs     # recompile, playMode, menu items, editor info
    ├── ManagedInvocationCommands.cs # reflection invoke + pending-invocation registry
    ├── Console/              # log buffer, get_logs, clear_logs (+ structured getLogs for sync_and_compile)
    └── Tests/                # run store + summary formatter, runTests, get_test_run_status, cancel_test_run
```

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
