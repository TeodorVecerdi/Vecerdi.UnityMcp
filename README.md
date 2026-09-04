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
together: one pin gives a project a matching pair.

## Requirements

- One of:
    - **Unity 6.5 or later** with [UnityRoslynUpdater](https://github.com/DaZombieKiller/UnityRoslynUpdater) to
      enable modern C# features (C# 13+) on the Mono runtime
    - **Unity 7 or later**, which runs on CoreCLR and ships the latest C# features out of the box
- [`Vecerdi.Extensions.Logging`](https://github.com/TeodorVecerdi/Vecerdi.Extensions.Logging), which has the same
  Unity requirements
- The `WebSocketSharp-netstandard` NuGet package (e.g. via NuGetForUnity) for the editor-side WebSocket server
- The **.NET 10 SDK** to build the bridge

## Installation

1. Put this repository under your project's `Assets/` (as a git submodule, or copied), for example
   `Assets/Scripts/Vecerdi.UnityMcp/`.
2. Make sure the dependencies listed under Requirements are present in the project.
3. Optionally add a `csc.rsp` next to the asmdef with your project's compiler conventions
   (nullable, language version, warnings-as-errors). The file is gitignored here on purpose so each
   host project keeps its own.
4. Build and publish the bridge, then point your MCP client at the published binary. See
   [`Bridge~/README.md`](Bridge~/README.md) for the build, the client configuration, the tool
   reference, and the editor plugin internals.

The plugin auto-starts with the editor (`[InitializeOnLoad]`) and shows its status under
**Window > Unity MCP Server**.

## Layout

```
Vecerdi.UnityMcp/
├── McpEditorServer.cs          # WebSocket server + command registration
├── McpServerWindow.cs          # status window
├── EditorInstanceRegistry.cs   # port allocation + discovery file
├── Protocol/                   # request/response types
├── Commands/                   # editor-side commands (each also an MCP tool descriptor)
└── Bridge~/                    # the MCP server: UnityMcp.slnx, src/, tests/
```

## License

[MIT](LICENSE).
