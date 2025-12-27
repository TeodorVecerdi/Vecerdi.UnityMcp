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
