using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
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

        CompilationPipeline.RequestScriptCompilation();
        return McpResponse.Ok(request.Id, new { requested = true });
    }
}

/// <summary>
/// Command: unity.editor.getCompilationStatus - Get current compilation state.
/// </summary>
public sealed class GetCompilationStatusCommand : IMcpCommandHandler {
    public string Command => "unity.editor.getCompilationStatus";

    public McpResponse Execute(McpRequest request) {
        var isCompiling = EditorApplication.isCompiling;

        // Get recent compilation errors from console if available
        // Note: This is a simplification - in practice you might want to
        // hook into CompilationPipeline.assemblyCompilationFinished for detailed errors

        return McpResponse.Ok(request.Id, new {
            isCompiling,
            // EditorApplication.isUpdating can indicate other background tasks
            isUpdating = EditorApplication.isUpdating,
        });
    }
}

/// <summary>
/// Command: unity.editor.isPlaying - Check if in play mode.
/// </summary>
public sealed class IsPlayingCommand : IMcpCommandHandler {
    public string Command => "unity.editor.isPlaying";

    public McpResponse Execute(McpRequest request) {
        return McpResponse.Ok(request.Id, new {
            isPlaying = EditorApplication.isPlaying,
            isPaused = EditorApplication.isPaused,
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
        });
    }
}

/// <summary>
/// Command: unity.editor.enterPlayMode - Enter play mode.
/// </summary>
public sealed class EnterPlayModeCommand : IMcpCommandHandler {
    public string Command => "unity.editor.enterPlayMode";

    public McpResponse Execute(McpRequest request) {
        if (EditorApplication.isPlaying) {
            return McpResponse.Ok(request.Id, new { entered = false, reason = "Already in play mode" });
        }

        if (EditorApplication.isCompiling) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                "Cannot enter play mode while compiling");
        }

        EditorApplication.EnterPlaymode();
        return McpResponse.Ok(request.Id, new { entered = true });
    }
}

/// <summary>
/// Command: unity.editor.exitPlayMode - Exit play mode.
/// </summary>
public sealed class ExitPlayModeCommand : IMcpCommandHandler {
    public string Command => "unity.editor.exitPlayMode";

    public McpResponse Execute(McpRequest request) {
        if (!EditorApplication.isPlaying) {
            return McpResponse.Ok(request.Id, new { exited = false, reason = "Not in play mode" });
        }

        EditorApplication.ExitPlaymode();
        return McpResponse.Ok(request.Id, new { exited = true });
    }
}

/// <summary>
/// Command: unity.editor.pausePlayMode - Pause play mode.
/// </summary>
public sealed class PausePlayModeCommand : IMcpCommandHandler {
    public string Command => "unity.editor.pausePlayMode";

    public McpResponse Execute(McpRequest request) {
        if (!EditorApplication.isPlaying) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported, "Not in play mode");
        }

        EditorApplication.isPaused = true;
        return McpResponse.Ok(request.Id, new { paused = true });
    }
}

/// <summary>
/// Command: unity.editor.resumePlayMode - Resume paused play mode.
/// </summary>
public sealed class ResumePlayModeCommand : IMcpCommandHandler {
    public string Command => "unity.editor.resumePlayMode";

    public McpResponse Execute(McpRequest request) {
        if (!EditorApplication.isPlaying) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported, "Not in play mode");
        }

        EditorApplication.isPaused = false;
        return McpResponse.Ok(request.Id, new { resumed = true });
    }
}

/// <summary>
/// Command: unity.editor.getOpenScenes - Get currently open scenes.
/// </summary>
public sealed class GetOpenScenesCommand : IMcpCommandHandler {
    public string Command => "unity.editor.getOpenScenes";

    public McpResponse Execute(McpRequest request) {
        var sceneCount = SceneManager.sceneCount;
        var scenes = Enumerable.Range(0, sceneCount)
            .Select(SceneManager.GetSceneAt)
            .Select(s => new {
                name = s.name,
                path = s.path,
                isLoaded = s.isLoaded,
                isDirty = s.isDirty,
                buildIndex = s.buildIndex,
            })
            .ToList();

        return McpResponse.Ok(request.Id, new { scenes });
    }
}

/// <summary>
/// Command: unity.editor.saveAll - Save all open scenes and assets.
/// </summary>
public sealed class SaveAllCommand : IMcpCommandHandler {
    public string Command => "unity.editor.saveAll";

    public McpResponse Execute(McpRequest request) {
        // Save all modified assets
        AssetDatabase.SaveAssets();

        // Save all open scenes
        EditorSceneManager.SaveOpenScenes();

        return McpResponse.Ok(request.Id, new { saved = true });
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
public sealed class ExecuteMenuItemCommand : IMcpCommandHandler {
    public string Command => "unity.editor.executeMenuItem";

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
