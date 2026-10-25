using UnityEditor;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.getPlayModeState - Check if in play mode.
/// </summary>
public sealed class GetPlayModeStateCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.getPlayModeState";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "get_play_mode_state",
        "Check if the Unity Editor is in play mode, paused, or stopped. Returns {isPlaying, isPaused, isPlayingOrWillChangePlaymode}.",
        """{"type":"object","properties":{}}""");

    public McpResponse Execute(McpRequest request) {
        return McpResponse.Ok(request.Id, new {
            isPlaying = EditorApplication.isPlaying,
            isPaused = EditorApplication.isPaused,
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
        });
    }
}