# Vecerdi.UnityMcp

A Model Context Protocol (MCP) server for the Unity Editor, so AI coding agents can make their edits
take effect, read compiler diagnostics and console output, run tests, invoke managed methods, and
drive play mode against a live editor.

It has two halves that ship together in this repository:

| Part                   | What it is                                                                                                  |
|------------------------|-------------------------------------------------------------------------------------------------------------|
| Editor plugin (root)   | An editor-only assembly (`Vecerdi.UnityMcp.asmdef`) that hosts a WebSocket server inside the editor, registers each running editor in a discovery file, and advertises its commands as MCP tools |
| Bridge (`Bridge~/`)    | A .NET console app that speaks MCP over stdio to the agent and forwards to the editor. Hidden from Unity by the trailing `~` |

The two talk over a small JSON protocol and change in lockstep, which is why they are versioned
together: one pin gives a project a matching pair. How they fit together, and how to add tools or work
on the bridge, is in [`Bridge~/README.md`](Bridge~/README.md).

## Requirements

- One of:
    - **Unity 6.5 or later** with [UnityRoslynUpdater](https://github.com/DaZombieKiller/UnityRoslynUpdater) to
      enable modern C# features (C# 13+) on the Mono runtime
    - **Unity 7 or later**, which runs on CoreCLR and ships the latest C# features out of the box
- [`Vecerdi.Extensions.Logging`](https://github.com/TeodorVecerdi/Vecerdi.Extensions.Logging), which has the same
  Unity requirements
- The `Vecerdi.Extensions.Lifetime` assembly from
  [`Vecerdi.Extensions.DependencyInjection`](https://github.com/TeodorVecerdi/Vecerdi.Extensions.DependencyInjection)
  (`Lifetime/`, no references of its own), which suspends the server on a code reload and stops it on quit
- The `WebSocketSharp-netstandard` NuGet package (e.g. via NuGetForUnity) for the editor-side WebSocket server
- The **.NET 10 SDK** to build the bridge, and **PowerShell 7** for its publish script

## Installation

1. Put this repository under your project's `Assets/` (as a git submodule, or copied), for example
   `Assets/Scripts/Vecerdi.UnityMcp/`.
2. Make sure the dependencies listed under Requirements are present in the project.
3. Optionally add a `csc.rsp` next to the asmdef with your project's compiler conventions
   (nullable, language version, warnings-as-errors). The file is gitignored here on purpose so each
   host project keeps its own.
4. [Publish the bridge](#publishing-the-bridge) and [point your MCP client at it](#connecting-a-client).

The plugin auto-starts with the editor (`[InitializeOnLoad]`) and shows its status under
**Window > Unity MCP Server**.

## Publishing the bridge

```powershell
./Bridge~/publish.ps1
```

This publishes the bridge to a stable location for MCP clients to launch:
`<artifacts>/publish/UnityMcp/release/unity-mcp.exe`. A hosting repository that defines `ArtifactsPath`
in a `Directory.Build.props` above this folder decides where `<artifacts>` is (MediaVault routes it to
`<repo>/artifacts/`); otherwise it is `Bridge~/artifacts/`. `-OutputPath` publishes somewhere else
and `-Configuration` picks the build configuration.

You can publish while MCP clients are running the bridge. A plain `dotnet publish` fails then, because
the running server keeps its files open. The script builds into a staging folder, sets any changed
file that is in use aside as `*.old`, and copies the new build in. Clients already running keep the
build they started with until you restart them, and the script lists them by PID. New clients get the
new build.

Republish after changing anything under `Bridge~/src/`. Changes to the editor plugin need no publish
and no client restart: the editor advertises its tools again after every recompile (see
[`Bridge~/README.md`](Bridge~/README.md)).

## Connecting a client

Point the client at the published binary over stdio. For Claude Code, in the project's `.mcp.json`:

```json
{
  "mcpServers": {
    "unity-mcp": {
      "type": "stdio",
      "command": "<repo>/artifacts/publish/UnityMcp/release/unity-mcp.exe"
    }
  }
}
```

| Environment variable  | Default       | Description                                                                |
|-----------------------|---------------|----------------------------------------------------------------------------|
| `UNITY_MCP_LOG_LEVEL` | `information` | Bridge log verbosity: `trace`, `debug`, `information`, `warning`, `error` |

The bridge logs only to stderr (stdout carries the protocol), and there is no log file. Claude Code
writes it, with its own tool-call tracing, to per-launch JSONL files under
`%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project-slug>\mcp-logs-unity-mcp\`.

## Tools

Built into the bridge, because they need its orchestration or state:

| Tool | Description |
|------|-------------|
| `sync_and_compile` | **The default "make my edits take effect" call.** Refresh, recompile, wait out the domain reload, and return fresh diagnostics, as one operation |
| `refresh_assets` | Refresh the asset database and report whether that triggered a compile (prefer `sync_and_compile` for code changes) |
| `get_logs` | Recent console logs, with timestamps and a note when the buffer wrapped |
| `invoke_managed_method` | Invoke a managed method via reflection with JSON arguments. Async results (Task, ValueTask, UniTask, Awaitable) are awaited up to `waitMs`, then backgrounded |
| `run_tests` | Run tests with filters, optionally waiting for completion |
| `get_test_run_status` | Status and results of a test run |
| `cancel_test_run` | Cancel an active test run |
| `list_editors` / `select_editor` | Discover running editors and set the default target |

Advertised by the editor (the live set is whatever the connected editor exposes):

| Tool | Description |
|------|-------------|
| `get_play_mode_state` / `set_play_mode` | Query, enter or exit play mode |
| `execute_menu_item` | Execute a menu item by path |
| `clear_logs` | Clear the log buffer |
| `get_invocation_result` | Poll a backgrounded `invoke_managed_method` call |

## How the tools behave

- **Making edits take effect:** call `sync_and_compile`. It drains any in-flight import or compile,
  refreshes assets, forces a recompile, waits out the domain reload, and returns **only** the
  compiler diagnostics from that compile (as `file(line,col): severity CODE: message`). Don't chain
  `refresh_assets` with a manual recompile; that race is what this tool exists to remove.
- **Domain reloads:** `sync_and_compile` blocks (up to ~3 minutes) and drives a domain reload. The
  editor keeps its discovery entry through the reload, flagged `reloading`, so any other call that
  arrives mid-reload waits for the editor to come back (up to a minute) and then sends, instead of
  failing with a connect error. A request already in flight when the socket dropped fails with a
  message naming the reload; for non-idempotent commands, treat it as "unknown whether it ran".
- **Joining a compile you didn't start:** if the editor is already compiling when `sync_and_compile`
  arrives, or starts while the tool is still draining (you focused the editor a moment ago, say), the
  tool rides that compile instead of forcing a second one. It takes the compile's own start time as
  the diagnostics marker, waits for it to settle, refreshes the asset database, and compiles again
  only if the refresh found newer edits. The result says which happened.
- **Choosing an editor:** a call with no `port` goes to the `select_editor` default, or to the only
  running editor when exactly one exists.
- **Per-editor state:** the default target and the "latest" test run are tracked per editor, and the
  latest run is undefined across a domain reload. Pass the `runId` from `run_tests` to be safe.
- **Main-thread execution:** the editor processes ~10 queued commands per frame on the main thread, so
  a slow `invoke_managed_method` stalls other queued calls to that editor. Calls are serialized, not
  parallel.
- **Async invokes never deadlock:** `invoke_managed_method` waits at most `waitMs` (default 2 s) for an
  async result (`Task`, `ValueTask`, `UniTask` or Unity's `Awaitable`, generic or not), then returns
  `{pending: true, invocationId}` and lets the main thread resume. That is what allows main-thread-bound
  continuations (UniTask/PlayerLoop, Awaitable, the Unity sync context) to complete. Poll
  `get_invocation_result` for the outcome (handed out once; entries expire after ~1 h or on domain
  reload).
- **Test filters:** `testNames` matches by prefix, so a class's full name runs all its methods. The
  result counts *matched-and-ran* separately from the whole-tree *discovered* count and echoes the
  resolved filter, so "5 passed" can't be mistaken for the wrong 5.

## Troubleshooting

### "Failed to connect to Unity Editor" / "No Unity Editor instances found"

1. Make sure the editor is running.
2. Check **Window > Unity MCP Server**; the server should show "Running".
3. Call `list_editors` to see discovered instances. Right after the editor starts, the first call can
   race discovery; retry once.
4. If a modal dialog is open in the editor, every call fails generically while `list_editors` still
   works. Dismiss the dialog.
5. "has been reloading the script domain for Ns" means the entry is stuck in `reloading`: the editor
   never came back from a domain reload (a dialog, a crash, or a very long import). Check the editor;
   `list_editors` shows the state and how long it has been in it.

### No logs appearing

The log buffer only captures logs while the plugin is active, so anything from before the server
started isn't available. Trigger something that logs, then call `get_logs`.

### A compile seems stuck

Check the Unity console for errors that prevent compilation. Use `sync_and_compile`, which already
folds in the asset refresh and waits out the reload, rather than chaining `refresh_assets` with a
manual recompile.

### The client still runs an old bridge after publishing

Clients keep the build they started with. Restart the client; the publish script lists the processes
still on the previous build.
