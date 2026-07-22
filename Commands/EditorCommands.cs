using UnityEditor;
using UnityEditor.Compilation;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.recompile - Force script recompilation.
/// </summary>
public sealed class RecompileCommand : IMcpCommandHandler {
    public string Command => "unity.editor.recompile";

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

/// <summary>
/// Command: unity.editor.getCompilationStatus - Get current compilation state.
/// Used internally by the MCP recompile tool.
/// </summary>
public sealed class GetCompilationStatusCommand : IMcpCommandHandler {
    public string Command => "unity.editor.getCompilationStatus";

    public McpResponse Execute(McpRequest request) {
        return McpResponse.Ok(request.Id, new {
            isCompiling = EditorApplication.isCompiling,
            isUpdating = EditorApplication.isUpdating,
        });
    }
}

/// <summary>
/// Command: unity.editor.isPlaying - Check if in play mode.
/// </summary>
public sealed class IsPlayingCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.isPlaying";

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

/// <summary>
/// Command: unity.editor.refreshAssets - Refresh the asset database.
/// </summary>
public sealed class RefreshAssetsCommand : IMcpCommandHandler {
    public string Command => "unity.editor.refreshAssets";

    public McpResponse Execute(McpRequest request) {
        AssetDatabase.Refresh();
        return McpResponse.Ok(request.Id, new { refreshed = true });
    }
}

/// <summary>
/// Command: unity.editor.executeMenuItem - Execute a Unity menu item by path.
/// </summary>
public sealed class ExecuteMenuItemCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.executeMenuItem";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "execute_menu_item",
        "Execute a Unity Editor menu item by its path (e.g., 'File/Save Project', 'Edit/Project Settings...', 'Window/General/Console').",
        """{"type":"object","properties":{"menuItem":{"type":"string","description":"The menu item path to execute (e.g., 'File/Save Project')"}},"required":["menuItem"]}""");

    public McpResponse Execute(McpRequest request) {
        var menuItem = request.GetParam<string>("menuItem");

        if (string.IsNullOrEmpty(menuItem)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "menuItem parameter is required");
        }

        var executed = EditorApplication.ExecuteMenuItem(menuItem);

        if (!executed) {
            return McpResponse.Fail(request.Id, McpErrorCodes.MenuItemNotFound, $"Menu item not found or could not be executed: {menuItem}");
        }

        return McpResponse.Ok(request.Id, new { executed = true, menuItem });
    }
}
