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
        [Description("Include stack traces for each log entry (very verbose; default: false)")] bool includeStackTraces = false,
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

                if (includeStackTraces &&
                    log.TryGetProperty("stackTrace", out var stackTrace) &&
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

        // Step 1: Trigger recompile (Unity side refreshes assets before requesting compilation).
        try {
            var recompileResponse = await unityClient.SendAsync("unity.editor.recompile", null, ct);
            if (!recompileResponse.Success && recompileResponse.Error is not null) {
                return Error(recompileResponse.Error.Message);
            }
        } catch {
            // Expected - connection drops during domain reload.
        }

        // Step 2: Wait for Unity to come back after domain reload.
        await Task.Delay(1000, ct);

        var reconnected = await unityClient.WaitForConnectionAsync(
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            ct
        );

        if (!reconnected) {
            return Error("Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.");
        }

        // Step 3: Wait for compilation to complete.
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

        // Step 4: Check for compilation errors.
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
    /// Set Unity play mode on or off.
    /// </summary>
    [McpServerTool(Name = "set_play_mode"), Description("Set Unity play mode state. Pass isPlaying=true to enter Play mode or false to return to Edit mode.")]
    public async Task<CallToolResult> SetPlayMode(
        [Description("Desired play mode state. true enters Play mode, false exits to Edit mode.")] bool isPlaying,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        var response = await unityClient.SendAsync("unity.editor.setPlayMode", new { isPlaying }, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) {
            return Error("Failed to set play mode.");
        }

        var changed = result.TryGetProperty("changed", out var changedElement) && changedElement.GetBoolean();
        var currentIsPlaying = result.TryGetProperty("isPlaying", out var playingElement)
            ? playingElement.GetBoolean()
            : isPlaying;

        if (changed) {
            return Success(currentIsPlaying ? "Entered Play mode." : "Exited Play mode.");
        }

        var reason = result.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : "No state change";
        return Success($"Play mode unchanged: {reason}");
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
    /// Invoke any managed method in the Unity process via reflection.
    /// </summary>
    [McpServerTool(Name = "invoke_managed_method"), Description("Invoke any managed method in the Unity process via reflection. Supports static/instance methods, overload disambiguation, generic args, and JSON arguments.")]
    public async Task<CallToolResult> InvokeManagedMethod(
        [Description("Fully-qualified type name (e.g., 'UnityEditor.EditorApplication')")] string typeName,
        [Description("Method name to invoke")] string methodName,
        [Description("Optional assembly short name if type resolution needs it")] string? assemblyName = null,
        [Description("Optional list of parameter type names to disambiguate overloads")] string[]? parameterTypeNames = null,
        [Description("Optional generic type argument names for generic methods")] string[]? genericTypeNames = null,
        [Description("JSON-serializable argument list")] object[]? arguments = null,
        [Description("Invoke as instance method instead of static")] bool invokeOnInstance = false,
        [Description("Constructor arguments when invokeOnInstance=true")] object[]? constructorArguments = null,
        [Description("Allow non-public members")] bool includeNonPublic = false,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        var parameters = new Dictionary<string, object?> {
            ["typeName"] = typeName,
            ["methodName"] = methodName,
            ["invokeOnInstance"] = invokeOnInstance,
            ["includeNonPublic"] = includeNonPublic,
            ["arguments"] = arguments ?? [],
            ["constructorArguments"] = constructorArguments ?? [],
        };

        if (!string.IsNullOrWhiteSpace(assemblyName)) parameters["assemblyName"] = assemblyName;
        if (parameterTypeNames is { Length: > 0 }) parameters["parameterTypeNames"] = parameterTypeNames;
        if (genericTypeNames is { Length: > 0 }) parameters["genericTypeNames"] = genericTypeNames;

        var response = await unityClient.SendAsync("unity.managed.invokeMethod", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) {
            return Success("Method invocation succeeded with no result payload.");
        }

        return Success(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Run Unity tests and optionally wait for completion.
    /// </summary>
    [McpServerTool(Name = "run_tests"), Description("Run Unity tests via TestRunner API. Supports filtering by mode, assemblies, test names, categories, and groups. Can wait for completion and return a summarized report.")]
    public async Task<CallToolResult> RunTests(
        [Description("Test mode: EditMode or PlayMode")] string testMode = "EditMode",
        [Description("Filter by test assembly names (without .dll)")] string[]? assemblyNames = null,
        [Description("Filter by full test names")] string[]? testNames = null,
        [Description("Filter by NUnit category names")] string[]? categoryNames = null,
        [Description("Filter by Unity test groups")] string[]? groupNames = null,
        [Description("Optional Unity build target name")] string? targetPlatform = null,
        [Description("Wait for completion before returning")] bool waitForCompletion = true,
        [Description("Polling interval in milliseconds when waiting")] int pollIntervalMs = 1000,
        [Description("Maximum wait time in seconds")] int timeoutSeconds = 300,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        var parameters = new Dictionary<string, object?> {
            ["testMode"] = testMode,
        };

        if (assemblyNames is { Length: > 0 }) parameters["assemblyNames"] = assemblyNames;
        if (testNames is { Length: > 0 }) parameters["testNames"] = testNames;
        if (categoryNames is { Length: > 0 }) parameters["categoryNames"] = categoryNames;
        if (groupNames is { Length: > 0 }) parameters["groupNames"] = groupNames;
        if (!string.IsNullOrWhiteSpace(targetPlatform)) parameters["targetPlatform"] = targetPlatform;

        var startResponse = await unityClient.SendAsync("unity.editor.runTests", parameters, ct);
        if (ToErrorResult(startResponse) is { } startError) return startError;

        if (startResponse.Result is not { } startResult) {
            return Error("Unity started test execution but did not return run metadata.");
        }

        var runId = startResult.TryGetProperty("runId", out var runIdElement) ? runIdElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(runId)) {
            return Error("Unity started test execution but did not return a runId.");
        }

        if (!waitForCompletion) {
            return Success($"Started Unity test run.\nrunId: {runId}");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var pollDelay = TimeSpan.FromMilliseconds(Math.Max(200, pollIntervalMs));
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            await Task.Delay(pollDelay, ct);

            var statusResponse = await unityClient.SendAsync("unity.editor.getTestRunStatus", new { runId }, ct);
            if (ToErrorResult(statusResponse) is { } statusError) return statusError;

            if (statusResponse.Result is not { } statusResult) {
                continue;
            }

            var status = statusResult.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : "unknown";

            if (!string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)) {
                return Success(FormatTestRunSummary(statusResult));
            }
        }

        return Error($"Timed out waiting for test run completion.\nrunId: {runId}\nUse get_test_run_status to poll manually.");
    }

    /// <summary>
    /// Get the status of a Unity test run.
    /// </summary>
    [McpServerTool(Name = "get_test_run_status"), Description("Get status and results for a Unity test run. If runId is omitted, returns the latest run.")]
    public async Task<CallToolResult> GetTestRunStatus(
        [Description("Optional run identifier returned by run_tests")] string? runId = null,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        object? parameters = string.IsNullOrWhiteSpace(runId) ? null : new { runId };
        var response = await unityClient.SendAsync("unity.editor.getTestRunStatus", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) {
            return Error("Unity returned no test run status payload.");
        }

        return Success(FormatTestRunSummary(result));
    }

    /// <summary>
    /// Cancel an active Unity test run.
    /// </summary>
    [McpServerTool(Name = "cancel_test_run"), Description("Cancel an active Unity test run. If runId is omitted, cancels the currently running run.")]
    public async Task<CallToolResult> CancelTestRun(
        [Description("Optional run identifier returned by run_tests")] string? runId = null,
        CancellationToken ct = default
    ) {
        if (await EnsureConnectedAsync(ct) is { } connectionError) return connectionError;

        object? parameters = string.IsNullOrWhiteSpace(runId) ? null : new { runId };
        var response = await unityClient.SendAsync("unity.editor.cancelTestRun", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        var cancelledRunId = runId;
        if (response.Result is { } result
            && result.TryGetProperty("runId", out var resultRunIdElement)
            && resultRunIdElement.ValueKind == JsonValueKind.String) {
            cancelledRunId = resultRunIdElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(cancelledRunId)) {
            return Success("Requested test run cancellation.");
        }

        return Success($"Requested cancellation for test run {cancelledRunId}.");
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

    private static string FormatTestRunSummary(JsonElement result) {
        var runId = result.TryGetProperty("runId", out var runIdElement) ? runIdElement.GetString() : "<unknown>";
        var status = result.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "unknown";

        var discovered = 0;
        var executed = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var inconclusive = 0;
        var other = 0;

        if (result.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object) {
            discovered = ReadInt(totals, "discovered");
            executed = ReadInt(totals, "executed");
            passed = ReadInt(totals, "passed");
            failed = ReadInt(totals, "failed");
            skipped = ReadInt(totals, "skipped");
            inconclusive = ReadInt(totals, "inconclusive");
            other = ReadInt(totals, "other");
        }

        var sb = new StringBuilder();
        var statusLabel = string.IsNullOrWhiteSpace(status)
            ? "UNKNOWN"
            : status.ToUpperInvariant();
        var summaryLine = failed > 0
            ? $"Unity tests {statusLabel}: {failed} failed, {passed} passed ({executed}/{discovered} executed)"
            : $"Unity tests {statusLabel}: {passed} passed ({executed}/{discovered} executed)";

        if (skipped > 0 || inconclusive > 0 || other > 0) {
            summaryLine += $" | skipped {skipped}, inconclusive {inconclusive}, other {other}";
        }

        sb.AppendLine(summaryLine);
        sb.AppendLine($"status: {status}");
        sb.AppendLine($"discovered: {discovered}, executed: {executed}, passed: {passed}, failed: {failed}, skipped: {skipped}, inconclusive: {inconclusive}, other: {other}");
        sb.AppendLine($"runId: {runId}");

        if (result.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(messageElement.GetString())) {
            sb.AppendLine();
            sb.AppendLine($"message: {messageElement.GetString()}");
        }

        if (result.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array && failures.GetArrayLength() > 0) {
            sb.AppendLine();
            sb.AppendLine("failures:");

            foreach (var failure in failures.EnumerateArray().Take(25)) {
                var name = failure.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : "<unknown test>";
                var message = failure.TryGetProperty("message", out var failureMessageElement) ? failureMessageElement.GetString() : null;
                var stackTrace = failure.TryGetProperty("stackTrace", out var stackTraceElement) ? stackTraceElement.GetString() : null;

                sb.AppendLine($"- {name}");
                if (!string.IsNullOrWhiteSpace(message)) {
                    sb.AppendLine($"  message:\n{message}");
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(stackTrace)) {
                    sb.AppendLine($"  stack: {stackTrace}");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private static int ReadInt(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
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
