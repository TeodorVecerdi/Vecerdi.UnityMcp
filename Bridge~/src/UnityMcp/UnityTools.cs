using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
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
    public async Task<CallToolResult> GetLogs(
        [Description("Maximum number of log entries to return (default: 100)")] int count = 100,
        [Description("Minimum log level to include: info, warning, or error")] string? minLevel = null,
        [Description("Filter logs containing this text (case-insensitive)")] string? filter = null,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        var parameters = new Dictionary<string, object?> { ["count"] = count };
        if (minLevel is not null) parameters["minLevel"] = minLevel;
        if (filter is not null) parameters["filter"] = filter;

        var response = await unityClient.SendAsync("unity.debug.getLogs", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Success("No logs available.");

        if (result.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array) {
            var logCount = logs.GetArrayLength();
            if (logCount == 0) return Success("No logs matching the criteria.");

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

            return Success(sb.ToString());
        }

        return Success(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Clear the Unity console log buffer.
    /// </summary>
    [McpServerTool(Name = "clear_logs"), Description("Clear the Unity console log buffer.")]
    public async Task<CallToolResult> ClearLogs(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.debug.clearLogs", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success("Log buffer cleared.");
    }

    /// <summary>
    /// Force Unity to recompile all scripts. This is a blocking call that waits for
    /// compilation to complete and returns any errors.
    /// </summary>
    [McpServerTool(Name = "recompile"), Description("Force Unity to recompile all scripts. Use this after making code changes to verify they compile. This is a blocking call that waits for compilation to complete and returns any errors.")]
    public async Task<CallToolResult> Recompile(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        // Step 1: Send recompile command (connection may drop due to domain reload)
        try {
            var recompileResponse = await unityClient.SendAsync("unity.editor.recompile", null, ct);
            
            // Check if the command failed (e.g., Unity is in Play Mode)
            if (!recompileResponse.Success && recompileResponse.Error is not null) {
                return Error(recompileResponse.Error.Message);
            }
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
            return Error("Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.");
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

                return Error(sb.ToString());
            }

            return Success("Compilation completed successfully with no errors.");
        } catch {
            return Error("Recompile triggered. Unable to verify completion status - check Unity Editor manually.");
        }
    }

    /// <summary>
    /// Check if Unity Editor is in play mode, paused, or stopped.
    /// </summary>
    [McpServerTool(Name = "get_play_mode_state"), Description("Check if Unity Editor is in play mode, paused, or stopped.")]
    public async Task<CallToolResult> GetPlayModeState(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.isPlaying", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Error("Unable to get play mode state.");

        var isPlaying = result.TryGetProperty("isPlaying", out var p) && p.GetBoolean();
        var isPaused = result.TryGetProperty("isPaused", out var pa) && pa.GetBoolean();

        if (!isPlaying) return Success("Unity is in Edit mode (not playing).");
        if (isPaused) return Success("Unity is in Play mode (PAUSED).");
        return Success("Unity is in Play mode (running).");
    }

    /// <summary>
    /// Start Play mode in the Unity Editor to test the game.
    /// </summary>
    [McpServerTool(Name = "enter_play_mode"), Description("Start Play mode in the Unity Editor to test the game.")]
    public async Task<CallToolResult> EnterPlayMode(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.enterPlayMode", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Error("Failed to enter play mode.");

        var entered = result.TryGetProperty("entered", out var e) && e.GetBoolean();
        if (entered) return Success("Entered Play mode.");

        var reason = result.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
        return Error($"Could not enter Play mode: {reason}");
    }

    /// <summary>
    /// Stop Play mode and return to Edit mode.
    /// </summary>
    [McpServerTool(Name = "exit_play_mode"), Description("Stop Play mode and return to Edit mode.")]
    public async Task<CallToolResult> ExitPlayMode(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.exitPlayMode", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Error("Failed to exit play mode.");

        var exited = result.TryGetProperty("exited", out var e) && e.GetBoolean();
        if (exited) return Success("Exited Play mode.");

        var reason = result.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
        return Error($"Could not exit Play mode: {reason}");
    }

    /// <summary>
    /// Pause the game while in Play mode.
    /// </summary>
    [McpServerTool(Name = "pause_play_mode"), Description("Pause the game while in Play mode.")]
    public async Task<CallToolResult> PausePlayMode(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.pausePlayMode", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success("Play mode paused.");
    }

    /// <summary>
    /// Resume the game after pausing in Play mode.
    /// </summary>
    [McpServerTool(Name = "resume_play_mode"), Description("Resume the game after pausing in Play mode.")]
    public async Task<CallToolResult> ResumePlayMode(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.resumePlayMode", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success("Play mode resumed.");
    }

    /// <summary>
    /// Get a list of currently open scenes in the Unity Editor.
    /// </summary>
    [McpServerTool(Name = "get_open_scenes"), Description("Get a list of currently open scenes in the Unity Editor.")]
    public async Task<CallToolResult> GetOpenScenes(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.getOpenScenes", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Error("Unable to get open scenes.");

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

            return Success(sb.ToString());
        }

        return Success("No scenes open.");
    }

    /// <summary>
    /// Save all open scenes and modified assets.
    /// </summary>
    [McpServerTool(Name = "save_all"), Description("Save all open scenes and modified assets.")]
    public async Task<CallToolResult> SaveAll(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.saveAll", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success("All scenes and assets saved.");
    }

    /// <summary>
    /// Refresh the Unity Asset Database to detect external file changes.
    /// </summary>
    [McpServerTool(Name = "refresh_assets"), Description("Refresh the Unity Asset Database to detect external file changes.")]
    public async Task<CallToolResult> RefreshAssets(CancellationToken ct = default) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.refreshAssets", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success("Asset database refreshed.");
    }

    /// <summary>
    /// Execute a Unity Editor menu item by its path.
    /// </summary>
    [McpServerTool(Name = "execute_menu_item"), Description("Execute a Unity Editor menu item by its path (e.g., 'File/Save Project', 'Edit/Project Settings...', 'Window/General/Console').")]
    public async Task<CallToolResult> ExecuteMenuItem(
        [Description("The menu item path to execute (e.g., 'File/Save Project')")] string menuItem,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;
        var response = await unityClient.SendAsync("unity.editor.executeMenuItem", new { menuItem }, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;
        return Success($"Executed menu item: {menuItem}");
    }

    /// <summary>
    /// List all available Unity Editor instances.
    /// </summary>
    [McpServerTool(Name = "list_editors"), Description("List all available Unity Editor instances that can be controlled via MCP.")]
    public CallToolResult ListEditors() {
        var editors = EditorDiscovery.GetAvailableEditors();

        if (editors.Count == 0) {
            return Error("No Unity Editor instances found. Make sure Unity is running with the MCP plugin installed.");
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

        return Success(sb.ToString());
    }

    /// <summary>
    /// Select which Unity Editor instance to control.
    /// </summary>
    [McpServerTool(Name = "select_editor"), Description("Select which Unity Editor instance to control. Use 'list_editors' first to see available instances.")]
    public async Task<CallToolResult> SelectEditor(
        [Description("The port number of the Unity Editor instance to connect to")] int port,
        CancellationToken ct = default
    ) {
        var editor = EditorDiscovery.FindEditorByPort(port);

        if (editor is null) {
            return Error($"No Unity Editor found on port {port}. Use 'list_editors' to see available instances.");
        }

        await unityClient.SetTargetAsync(editor);

        try {
            await unityClient.ConnectAsync(ct);
            return Success($"Connected to Unity Editor: {editor.ProjectName} (port {port})");
        } catch (Exception ex) {
            return Error($"Found editor on port {port} but failed to connect: {ex.Message}");
        }
    }

    // Helper methods
    private async Task<CallToolResult?> EnsureConnectedAsync(CancellationToken ct) {
        if (unityClient.IsConnected) return null;

        // Auto-discover and connect if there's exactly one editor available
        var editors = EditorDiscovery.GetAvailableEditors();
        if (editors.Count == 1) {
            await unityClient.SetTargetAsync(editors[0]);
        } else if (editors.Count > 1) {
            return Error($"Multiple Unity Editors found ({editors.Count}). Use 'list_editors' to see them and 'select_editor' to choose one.");
        }

        try {
            await unityClient.ConnectAsync(ct);
            return null;
        } catch (Exception ex) {
            var hint = editors.Count == 0
                ? "Make sure Unity Editor is running and the MCP plugin is active."
                : $"Found {editors.Count} editor(s) but failed to connect.";

            return Error($"Failed to connect to Unity Editor: {ex.Message}\n\n{hint}");
        }
    }

    private static CallToolResult? ToErrorResult(UnityResponse response) {
        if (response.Success) return null;

        var errorText = response.Error is not null
            ? $"Unity error [{response.Error.Code}]: {response.Error.Message}"
            : "Unity command failed with unknown error";

        return Error(errorText);
    }

    private static CallToolResult Success(string message) => new() {
        Content = [new TextContentBlock { Text = message }],
        IsError = false,
    };

    private static CallToolResult Error(string message) => new() {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };
}
