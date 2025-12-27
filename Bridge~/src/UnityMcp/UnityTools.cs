using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMcp;

/// <summary>
/// MCP tools for interacting with Unity Editor.
/// </summary>
[McpServerToolType]
public sealed class UnityTools(UnityClient unityClient) {
    /// <summary>
    /// Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.
    /// </summary>
    [McpServerTool(Name = "get_logs"), Description("Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.")]
    public async Task<string> GetLogs(
        [Description("Maximum number of log entries to return (default: 100)")] int count = 100,
        [Description("Minimum log level to include: info, warning, or error")] string? minLevel = null,
        [Description("Filter logs containing this text (case-insensitive)")] string? filter = null,
        CancellationToken ct = default
    ) {
        await EnsureConnectedAsync(ct);

        var parameters = new Dictionary<string, object?> { ["count"] = count };
        if (minLevel is not null) parameters["minLevel"] = minLevel;
        if (filter is not null) parameters["filter"] = filter;

        var response = await unityClient.SendAsync("unity.debug.getLogs", parameters, ct);
        EnsureSuccess(response);

        if (response.Result is not { } result) return "No logs available.";

        if (result.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array) {
            var logCount = logs.GetArrayLength();
            if (logCount == 0) return "No logs matching the criteria.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {logCount} log entries:");
            sb.AppendLine();

            foreach (var log in logs.EnumerateArray()) {
                var level = log.GetProperty("level").GetString()?.ToUpper() ?? "INFO";
                var message = log.GetProperty("message").GetString() ?? "";
                sb.AppendLine($"[{level}] {message}");

                if (log.TryGetProperty("stackTrace", out var stackTrace) &&
                    stackTrace.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(stackTrace.GetString())) {
                    sb.AppendLine($"  Stack: {stackTrace.GetString()}");
                }
            }

            return sb.ToString();
        }

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Clear the Unity console log buffer.
    /// </summary>
    [McpServerTool(Name = "clear_logs"), Description("Clear the Unity console log buffer.")]
    public async Task<string> ClearLogs(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.debug.clearLogs", null, ct);
        EnsureSuccess(response);
        return "Log buffer cleared.";
    }

    /// <summary>
    /// Force Unity to recompile all scripts. This is a blocking call that waits for
    /// compilation to complete and returns any errors.
    /// </summary>
    [McpServerTool(Name = "recompile"), Description("Force Unity to recompile all scripts. Use this after making code changes to verify they compile. This is a blocking call that waits for compilation to complete and returns any errors.")]
    public async Task<string> Recompile(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);

        // Step 1: Send recompile command (connection may drop due to domain reload)
        try {
            await unityClient.SendAsync("unity.editor.recompile", null, ct);
        } catch {
            // Expected - connection drops during domain reload
        }

        // Step 2: Wait for Unity to come back after domain reload
        await Task.Delay(1000, ct);

        var reconnected = await unityClient.WaitForConnectionAsync(
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            ct
        );

        if (!reconnected) {
            return "Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.";
        }

        // Step 3: Wait for compilation to complete
        await Task.Delay(500, ct);

        var compilationTimeout = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < compilationTimeout && !ct.IsCancellationRequested) {
            try {
                var statusResponse = await unityClient.SendAsync("unity.editor.getCompilationStatus", null, ct);
                if (statusResponse is { Success: true, Result: not null }) {
                    var isCompiling = statusResponse.Result.Value.TryGetProperty("isCompiling", out var c) && c.GetBoolean();
                    var isUpdating = statusResponse.Result.Value.TryGetProperty("isUpdating", out var u) && u.GetBoolean();

                    if (!isCompiling && !isUpdating) break;
                }
            } catch {
                await unityClient.WaitForConnectionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), ct);
            }

            await Task.Delay(1000, ct);
        }

        // Step 4: Check for compilation errors
        try {
            var logsResponse = await unityClient.SendAsync("unity.debug.getLogs", new { count = 100, minLevel = "error" }, ct);

            if (logsResponse is { Success: true, Result: not null } && logsResponse.Result.Value.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array && logs.GetArrayLength() > 0) {
                var sb = new StringBuilder();
                sb.AppendLine("Compilation completed with errors:");
                sb.AppendLine();

                foreach (var logEntry in logs.EnumerateArray()) {
                    var message = logEntry.TryGetProperty("message", out var m) ? m.GetString() : "";
                    sb.AppendLine($"[ERROR] {message}");

                    if (logEntry.TryGetProperty("stackTrace", out var stackTrace) &&
                        stackTrace.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(stackTrace.GetString())) {
                        sb.AppendLine($"  {stackTrace.GetString()}");
                    }
                }

                return sb.ToString();
            }

            return "Compilation completed successfully with no errors.";
        } catch {
            return "Recompile triggered. Unable to verify completion status - check Unity Editor manually.";
        }
    }

    /// <summary>
    /// Check if Unity Editor is in play mode, paused, or stopped.
    /// </summary>
    [McpServerTool(Name = "get_play_mode_state"), Description("Check if Unity Editor is in play mode, paused, or stopped.")]
    public async Task<string> GetPlayModeState(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.isPlaying", null, ct);
        EnsureSuccess(response);

        if (response.Result is not { } result) return "Unable to get play mode state.";

        var isPlaying = result.TryGetProperty("isPlaying", out var p) && p.GetBoolean();
        var isPaused = result.TryGetProperty("isPaused", out var pa) && pa.GetBoolean();

        if (!isPlaying) return "Unity is in Edit mode (not playing).";
        if (isPaused) return "Unity is in Play mode (PAUSED).";
        return "Unity is in Play mode (running).";
    }

    /// <summary>
    /// Start Play mode in the Unity Editor to test the game.
    /// </summary>
    [McpServerTool(Name = "enter_play_mode"), Description("Start Play mode in the Unity Editor to test the game.")]
    public async Task<string> EnterPlayMode(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.enterPlayMode", null, ct);
        EnsureSuccess(response);

        if (response.Result is not { } result) return "Failed to enter play mode.";

        var entered = result.TryGetProperty("entered", out var e) && e.GetBoolean();
        if (entered) return "Entered Play mode.";

        var reason = result.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
        return $"Could not enter Play mode: {reason}";
    }

    /// <summary>
    /// Stop Play mode and return to Edit mode.
    /// </summary>
    [McpServerTool(Name = "exit_play_mode"), Description("Stop Play mode and return to Edit mode.")]
    public async Task<string> ExitPlayMode(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.exitPlayMode", null, ct);
        EnsureSuccess(response);

        if (response.Result is not { } result) return "Failed to exit play mode.";

        var exited = result.TryGetProperty("exited", out var e) && e.GetBoolean();
        if (exited) return "Exited Play mode.";

        var reason = result.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
        return $"Could not exit Play mode: {reason}";
    }

    /// <summary>
    /// Pause the game while in Play mode.
    /// </summary>
    [McpServerTool(Name = "pause_play_mode"), Description("Pause the game while in Play mode.")]
    public async Task<string> PausePlayMode(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.pausePlayMode", null, ct);
        EnsureSuccess(response);
        return "Play mode paused.";
    }

    /// <summary>
    /// Resume the game after pausing in Play mode.
    /// </summary>
    [McpServerTool(Name = "resume_play_mode"), Description("Resume the game after pausing in Play mode.")]
    public async Task<string> ResumePlayMode(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.resumePlayMode", null, ct);
        EnsureSuccess(response);
        return "Play mode resumed.";
    }

    /// <summary>
    /// Get a list of currently open scenes in the Unity Editor.
    /// </summary>
    [McpServerTool(Name = "get_open_scenes"), Description("Get a list of currently open scenes in the Unity Editor.")]
    public async Task<string> GetOpenScenes(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.getOpenScenes", null, ct);
        EnsureSuccess(response);

        if (response.Result is not { } result) return "Unable to get open scenes.";

        if (result.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array) {
            var sb = new StringBuilder();
            sb.AppendLine("Open scenes:");

            foreach (var scene in scenes.EnumerateArray()) {
                var name = scene.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
                var path = scene.TryGetProperty("path", out var p) ? p.GetString() : "";
                var isDirty = scene.TryGetProperty("isDirty", out var d) && d.GetBoolean();

                sb.AppendLine($"  - {name}{(isDirty ? " (unsaved)" : "")}");
                if (!string.IsNullOrEmpty(path)) {
                    sb.AppendLine($"    Path: {path}");
                }
            }

            return sb.ToString();
        }

        return "No scenes open.";
    }

    /// <summary>
    /// Save all open scenes and modified assets.
    /// </summary>
    [McpServerTool(Name = "save_all"), Description("Save all open scenes and modified assets.")]
    public async Task<string> SaveAll(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.saveAll", null, ct);
        EnsureSuccess(response);
        return "All scenes and assets saved.";
    }

    /// <summary>
    /// Refresh the Unity Asset Database to detect external file changes.
    /// </summary>
    [McpServerTool(Name = "refresh_assets"), Description("Refresh the Unity Asset Database to detect external file changes.")]
    public async Task<string> RefreshAssets(CancellationToken ct = default) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.refreshAssets", null, ct);
        EnsureSuccess(response);
        return "Asset database refreshed.";
    }

    /// <summary>
    /// Execute a Unity Editor menu item by its path.
    /// </summary>
    [McpServerTool(Name = "execute_menu_item"), Description("Execute a Unity Editor menu item by its path (e.g., 'File/Save Project', 'Edit/Project Settings...', 'Window/General/Console').")]
    public async Task<string> ExecuteMenuItem(
        [Description("The menu item path to execute (e.g., 'File/Save Project')")] string menuItem,
        CancellationToken ct = default
    ) {
        await EnsureConnectedAsync(ct);
        var response = await unityClient.SendAsync("unity.editor.executeMenuItem", new { menuItem }, ct);
        EnsureSuccess(response);
        return $"Executed menu item: {menuItem}";
    }

    /// <summary>
    /// List all available Unity Editor instances.
    /// </summary>
    [McpServerTool(Name = "list_editors"), Description("List all available Unity Editor instances that can be controlled via MCP.")]
    public string ListEditors() {
        var editors = EditorDiscovery.GetAvailableEditors();

        if (editors.Count == 0) {
            return "No Unity Editor instances found. Make sure Unity is running with the MCP plugin installed.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {editors.Count} Unity Editor instance(s):");
        sb.AppendLine();

        var currentUri = unityClient.CurrentUri;

        foreach (var editor in editors) {
            var uri = EditorDiscovery.GetEditorUri(editor);
            var isConnected = uri == currentUri && unityClient.IsConnected;
            var marker = isConnected ? " [CONNECTED]" : "";

            sb.AppendLine($"  Port {editor.Port}:{marker}");
            sb.AppendLine($"    Project: {editor.ProjectName}");
            sb.AppendLine($"    Path: {editor.ProjectPath}");
            sb.AppendLine($"    PID: {editor.ProcessId}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Select which Unity Editor instance to control.
    /// </summary>
    [McpServerTool(Name = "select_editor"), Description("Select which Unity Editor instance to control. Use 'list_editors' first to see available instances.")]
    public async Task<string> SelectEditor(
        [Description("The port number of the Unity Editor instance to connect to")] int port,
        CancellationToken ct = default
    ) {
        var editor = EditorDiscovery.FindEditorByPort(port);

        if (editor is null) {
            return $"No Unity Editor found on port {port}. Use 'list_editors' to see available instances.";
        }

        await unityClient.SetTargetAsync(editor);

        try {
            await unityClient.ConnectAsync(ct);
            return $"Connected to Unity Editor: {editor.ProjectName} (port {port})";
        } catch (Exception ex) {
            return $"Found editor on port {port} but failed to connect: {ex.Message}";
        }
    }

    // Helper methods
    private async Task EnsureConnectedAsync(CancellationToken ct) {
        if (unityClient.IsConnected) return;

        // Auto-discover and connect if there's exactly one editor available
        var editors = EditorDiscovery.GetAvailableEditors();
        if (editors.Count == 1) {
            await unityClient.SetTargetAsync(editors[0]);
        } else if (editors.Count > 1) {
            throw new InvalidOperationException(
                $"Multiple Unity Editors found ({editors.Count}). Use 'list_editors' to see them and 'select_editor' to choose one."
            );
        }

        try {
            await unityClient.ConnectAsync(ct);
        } catch (Exception ex) {
            var hint = editors.Count == 0
                ? "Make sure Unity Editor is running and the MCP plugin is active."
                : $"Found {editors.Count} editor(s) but failed to connect.";

            throw new InvalidOperationException(
                $"Failed to connect to Unity Editor: {ex.Message}\n\n{hint}",
                ex
            );
        }
    }

    private static void EnsureSuccess(UnityResponse response) {
        if (response.Success) return;

        var errorText = response.Error is not null
            ? $"Unity error [{response.Error.Code}]: {response.Error.Message}"
            : "Unity command failed with unknown error";

        throw new InvalidOperationException(errorText);
    }
}
