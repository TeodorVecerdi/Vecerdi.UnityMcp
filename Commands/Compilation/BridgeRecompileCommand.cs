using UnityEditor;
using UnityEditor.Compilation;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.bridge.recompile - Force script recompilation.
/// </summary>
public sealed class BridgeRecompileCommand : IMcpCommandHandler {
    public string Command => "unity.bridge.recompile";

    public McpResponse Execute(McpRequest request) {
        if (EditorApplication.isPlaying) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                "Cannot recompile: Unity is in Play Mode. Recompilation is disabled during play mode to prevent data loss.");
        }

        AssetDatabase.Refresh();
        CompilationPipeline.RequestScriptCompilation();
        return McpResponse.Ok(request.Id, new {
            refreshed = true,
            requested = true,
        });
    }
}
