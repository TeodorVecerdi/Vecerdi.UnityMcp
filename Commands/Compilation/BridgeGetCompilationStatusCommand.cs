using UnityEditor;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.bridge.getCompilationStatus - Get current compilation state.
/// Used internally by the MCP recompile tool.
/// </summary>
public sealed class BridgeGetCompilationStatusCommand : IMcpCommandHandler {
    public string Command => "unity.bridge.getCompilationStatus";

    public McpResponse Execute(McpRequest request) {
        return McpResponse.Ok(request.Id, new {
            isCompiling = EditorApplication.isCompiling,
            isUpdating = EditorApplication.isUpdating,
        });
    }
}