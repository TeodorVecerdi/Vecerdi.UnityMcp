using System;
using System.Collections.Generic;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Handler for a specific MCP command.
/// </summary>
public interface IMcpCommandHandler {
    /// <summary>The command path this handler responds to (e.g., "unity.debug.getLogs").</summary>
    string Command { get; }

    /// <summary>Execute the command and return a response.</summary>
    McpResponse Execute(McpRequest request);
}

/// <summary>
/// How a command presents itself as an agent-facing MCP tool (todo #243). The stdio bridge
/// fetches these via <c>unity.meta.listTools</c> when it connects and serves them as dynamic
/// tools, so adding a tool needs no bridge rebuild. The bridge injects its own <c>port</c>
/// parameter into every schema; the response the agent sees is this command's raw result JSON
/// (no bridge-side formatting) — write results to be self-describing.
/// </summary>
/// <param name="Name">Agent-facing tool name (snake_case, e.g. "set_play_mode").</param>
/// <param name="Description">Tool description shown to agents; carries the full usage contract.</param>
/// <param name="InputSchemaJson">JSON Schema object for the command's parameters (excluding the bridge's port).</param>
public sealed record McpToolDescriptor(string Name, string Description, string InputSchemaJson);

/// <summary>Implemented by command handlers that should be discoverable as MCP tools.</summary>
public interface IMcpToolProvider {
    McpToolDescriptor ToolDescriptor { get; }
}

/// <summary>
/// Registry and dispatcher for MCP command handlers.
/// </summary>
public sealed class McpCommandRegistry {
    private readonly Dictionary<string, IMcpCommandHandler> m_Handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a command handler.</summary>
    public void Register(IMcpCommandHandler handler) {
        m_Handlers[handler.Command] = handler;
    }

    /// <summary>Register multiple handlers.</summary>
    public void Register(params IMcpCommandHandler[] handlers) {
        foreach (var handler in handlers) {
            Register(handler);
        }
    }

    /// <summary>Check if a command is registered.</summary>
    public bool HasCommand(string command) => m_Handlers.ContainsKey(command);

    /// <summary>Get all registered command names.</summary>
    public IEnumerable<string> GetCommands() => m_Handlers.Keys;

    /// <summary>Get all registered handlers (for tool discovery).</summary>
    public IEnumerable<IMcpCommandHandler> GetHandlers() => m_Handlers.Values;

    /// <summary>Execute a command request.</summary>
    public McpResponse Execute(McpRequest request) {
        if (string.IsNullOrEmpty(request.Command)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "Command is required.");
        }

        if (!m_Handlers.TryGetValue(request.Command, out var handler)) {
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.CommandNotFound,
                $"Unknown command: {request.Command}",
                new { availableCommands = GetCommands() }
            );
        }

        try {
            return handler.Execute(request);
        } catch (Exception ex) {
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.ExecutionFailed,
                $"Command execution failed: {ex.Message}",
                new { exception = ex.GetType().Name, stackTrace = ex.StackTrace }
            );
        }
    }
}
