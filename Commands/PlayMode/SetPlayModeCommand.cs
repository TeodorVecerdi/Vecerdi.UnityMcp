using UnityEditor;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.setPlayMode - Enter or exit play mode.
/// </summary>
public sealed class SetPlayModeCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.setPlayMode";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "set_play_mode",
        "Set Unity play mode state. Pass isPlaying=true to enter Play mode or false to return to Edit mode. Returns {changed, isPlaying} (plus a reason when nothing changed); fails while the editor is compiling or updating.",
        """{"type":"object","properties":{"isPlaying":{"type":"boolean","description":"Desired play mode state. true enters Play mode, false exits to Edit mode."}},"required":["isPlaying"]}""");

    public McpResponse Execute(McpRequest request) {
        if (!request.HasParam("isPlaying")) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "isPlaying parameter is required");
        }

        var shouldPlay = request.GetParam<bool>("isPlaying");

        if (shouldPlay) {
            if (EditorApplication.isPlaying) {
                return McpResponse.Ok(request.Id, new {
                    changed = false,
                    isPlaying = true,
                    reason = "Already in play mode",
                });
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) {
                return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                    "Cannot enter play mode while Unity is compiling or updating");
            }

            EditorApplication.EnterPlaymode();
            return McpResponse.Ok(request.Id, new { changed = true, isPlaying = true });
        }

        if (!EditorApplication.isPlaying) {
            return McpResponse.Ok(request.Id, new {
                changed = false,
                isPlaying = false,
                reason = "Already in edit mode",
            });
        }

        EditorApplication.ExitPlaymode();
        return McpResponse.Ok(request.Id, new { changed = true, isPlaying = false });
    }
}