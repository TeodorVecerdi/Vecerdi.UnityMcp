using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Unity.Scripting.LifecycleManagement;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.debug.clearLogs - Clear the log buffer and Unity Console.
/// </summary>
[NoAutoStaticsCleanup]
public sealed class ClearLogsCommand(LogBuffer logBuffer, ILogger? logger = null) : IMcpCommandHandler, IMcpToolProvider {
    private static readonly MethodInfo? s_ClearMethod = Type.GetType("UnityEditor.LogEntries, UnityEditor")?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
    public string Command => "unity.debug.clearLogs";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "clear_logs",
        "Clear the Unity console log buffer.",
        """{"type":"object","properties":{}}""");

    public McpResponse Execute(McpRequest request) {
        // Clear our internal buffer
        logBuffer.Clear();

        // Clear Unity's Console window using reflection (internal API)
        try {
            s_ClearMethod?.Invoke(null, null);
        } catch (Exception ex) {
            logger?.LogWarning(ex, "Failed to clear Unity Console");
        }

        return McpResponse.Ok(request.Id, new { cleared = true });
    }
}
