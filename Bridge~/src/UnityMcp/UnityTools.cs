using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityMcp;

/// <summary>
/// MCP tools for interacting with Unity Editor.
/// </summary>
[McpServerToolType]
public sealed class UnityTools(UnityConnectionPool pool) {
    private const string PortParamDescription =
        "Optional Unity Editor port. Omitted: targets the 'select_editor' default, else the only running editor; " +
        "required when several run and no default is set ('list_editors' shows ports). Editor state (default " +
        "selection, latest test run) is per-editor. A call that arrives while the editor is reloading the script " +
        "domain waits for it to come back (up to a minute) before sending.";

    // Non-indented output: System.Text.Json's default encoder escapes '+', '<', '>', '&' and all non-ASCII to
    // \uXXXX, which mangles printable characters in returned strings. The relaxed encoder emits them verbatim.
    private static readonly JsonSerializerOptions s_OutputJson = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.
    /// </summary>
    [McpServerTool(Name = "get_logs"), Description("Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.")]
    public async Task<CallToolResult> GetLogs(
        [Description("Maximum number of log entries to return (default: 100)")] int count = 100,
        [Description("Minimum log level to include: info, warning, or error")] string? minLevel = null,
        [Description("Filter logs containing this text (case-insensitive)")] string? filter = null,
        [Description("Include stack traces for each log entry (very verbose; default: false)")] bool includeStackTraces = false,
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

        var parameters = new Dictionary<string, object?> { ["count"] = count };
        if (minLevel is not null) parameters["minLevel"] = minLevel;
        if (filter is not null) parameters["filter"] = filter;

        var response = await unity!.SendAsync("unity.debug.getLogs", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) return Success("No logs available.");

        if (result.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array) {
            var records = UnityLogRecord.ReadAll(result);
            var bufferInfo = LogBufferInfo.FromResult(result);
            if (records.Count == 0) {
                var emptyNote = bufferInfo.Note();
                return Success(emptyNote is null
                    ? "No logs matching the criteria."
                    : $"No logs matching the criteria.\n{emptyNote}");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {records.Count} log entries:");
            var note = bufferInfo.Note();
            if (note is not null) sb.AppendLine(note);
            sb.AppendLine();

            foreach (var record in records) {
                sb.AppendLine($"[{record.TimestampLabel()}] [{record.Level.ToUpperInvariant()}] {record.Message}");

                if (includeStackTraces && !string.IsNullOrEmpty(record.StackTrace)) {
                    sb.AppendLine($"  Stack: {record.StackTrace}");
                }
            }

            return Success(sb.ToString());
        }

        return Success(JsonSerializer.Serialize(result, s_OutputJson));
    }

    // clear_logs, execute_menu_item, get_play_mode_state, set_play_mode, and
    // get_invocation_result are served dynamically (#243): the editor advertises them via
    // unity.meta.listTools and DynamicToolManager registers them on connect. Only tools with
    // bridge-side logic (multi-step waits, formatting, discovery) stay native here.

    private const string SyncAndCompileDescription =
        "THE default 'make my code edits take effect' call: waits out any in-flight import/compile, refreshes the " +
        "Asset Database, forces a recompile, waits for the domain reload, and returns ONLY the compiler diagnostics " +
        "produced by THIS compile (never stale console errors). Absorbs the refresh internally - do not call " +
        "refresh_assets first. Blocks up to ~3 minutes and drives a domain reload; other calls to the same editor " +
        "fail with connect errors until it returns.";

    /// <summary>
    /// Unified refresh + recompile. Waits for in-flight compilation to settle, refreshes assets, forces a
    /// recompile, waits for completion, and reports only fresh compiler diagnostics.
    /// </summary>
    [McpServerTool(Name = "sync_and_compile"), Description(SyncAndCompileDescription)]
    public async Task<CallToolResult> SyncAndCompile(
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

        var targetPort = EditorDiscovery.TryGetPort(unity!.CurrentUri);
        return await RunSyncAndCompileAsync(unity, () => targetPort is { } p ? EditorDiscovery.FindEditorByPort(p) : null, ct);
    }

    /// <summary>
    /// The self-contained refresh -> wait -> compile -> wait -> fresh-diagnostics operation behind
    /// <c>sync_and_compile</c>. Never trips over an in-flight compile because it
    /// drains any pending import/compile before starting its own, and marks the log-buffer position so only
    /// diagnostics produced by this compile are returned.
    /// </summary>
    /// <param name="registryProbe">Reads the editor's current discovery entry; used to join a compile already in flight.</param>
    internal static async Task<CallToolResult> RunSyncAndCompileAsync(IUnityConnection unity, Func<EditorInstance?> registryProbe, CancellationToken ct) {
        // Step 0: Is a compile we did not start already running (the user focused the editor a moment ago, say)?
        // If the editor advertised when it started, join it instead of paying for a second one.
        if (DetectInFlightCompile(registryProbe()) is { } inFlight) {
            return await JoinInFlightCompileAsync(unity, inFlight, ct);
        }

        // Drain any import/compile already in flight whose start we cannot place (older plugin, or a bare asset
        // import), so our forced recompile does not collide with it. Best-effort; a timeout here is not fatal.
        await WaitForCompilationIdleAsync(unity, TimeSpan.FromSeconds(60), ct);

        // Step 1: Mark the buffer position. Only diagnostics stamped strictly after this are "fresh".
        var marker = DateTimeOffset.UtcNow;

        // Step 2: Trigger recompile. The plugin's recompile command refreshes the Asset Database and then
        // calls RequestScriptCompilation, so this single command performs both the refresh and the compile.
        try {
            var recompileResponse = await unity.SendAsync("unity.editor.recompile", null, ct);
            if (!recompileResponse.Success && recompileResponse.Error is not null) {
                // e.g. "cannot recompile in play mode" - surface directly; nothing was triggered.
                return Error(recompileResponse.Error.Message);
            }
        } catch {
            // Expected - the connection can drop as the domain reloads.
        }

        // Step 3: Wait for Unity to come back after a possible domain reload.
        await Task.Delay(1000, ct);
        var reconnected = await unity.WaitForConnectionAsync(
            timeout: TimeSpan.FromSeconds(60),
            pollInterval: TimeSpan.FromMilliseconds(500),
            ct);
        if (!reconnected) {
            return Error("Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.");
        }

        // Step 4: Wait for compilation + asset import to finish.
        await Task.Delay(500, ct);
        var settled = await WaitForCompilationIdleAsync(unity, TimeSpan.FromSeconds(120), ct);

        // Step 5: Report only diagnostics produced after the marker.
        return await BuildFreshDiagnosticsResultAsync(unity, marker, settled, ct);
    }

    /// <summary>
    /// A compile that was already running when a <c>sync_and_compile</c> call arrived, as far as the discovery
    /// entry can tell: the editor is <c>compiling</c>, or <c>reloading</c> because of a compile, and it recorded
    /// when that compile started. Anything less (older plugin, a bare asset import, a reload no compile caused)
    /// yields null and the caller falls back to draining and forcing its own compile.
    /// </summary>
    internal static DateTimeOffset? DetectInFlightCompile(EditorInstance? entry) =>
        entry is { CompilationStartedAt: { } startedAt } && (entry.IsCompiling || entry.IsReloading) ? startedAt : null;

    /// <summary>
    /// Ride a compile that is already in flight instead of starting another: wait for it to settle, ask the asset
    /// database whether anything changed since it began (the editor's own dirty check), compile again only if that
    /// refresh kicked one off, and report every diagnostic since the original compile started.
    /// </summary>
    private static async Task<CallToolResult> JoinInFlightCompileAsync(IUnityConnection unity, DateTimeOffset compileStartedAt, CancellationToken ct) {
        // The marker is the in-flight compile's own start, so its errors count as fresh even though we did not ask
        // for them. Its per-assembly errors may already be in the buffer by now; that is exactly why the arrival
        // time would be the wrong marker.
        var marker = compileStartedAt;
        var joinedAgo = DateTimeOffset.UtcNow - compileStartedAt;

        var settled = await WaitForCompilationIdleAsync(unity, TimeSpan.FromSeconds(120), ct);

        // Anything edited after that compile began is still unimported. Refresh; if Unity finds work, a second
        // compile (and possibly a reload) follows and the marker covers it too.
        var refreshTriggeredMore = true;
        try {
            var refreshResponse = await unity.SendAsync("unity.editor.refreshAssets", null, ct);
            if (refreshResponse.Success) {
                await Task.Delay(250, ct);
                refreshTriggeredMore = await IsCompilingOrUpdatingAsync(unity, ct) != false;
            }
        } catch {
            // Connection dropped: the refresh itself started a reload.
        }

        if (refreshTriggeredMore) {
            await Task.Delay(1000, ct);
            var reconnected = await unity.WaitForConnectionAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500), ct);
            if (!reconnected) {
                return Error("Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.");
            }

            await Task.Delay(500, ct);
            settled = await WaitForCompilationIdleAsync(unity, TimeSpan.FromSeconds(120), ct);
        }

        var note = refreshTriggeredMore
            ? $"Joined a compile that was already running (started {joinedAgo.TotalSeconds:F0}s before this call); newer edits triggered a further compile."
            : $"Joined a compile that was already running (started {joinedAgo.TotalSeconds:F0}s before this call); no edits were newer than it, so no second compile was needed.";
        return await BuildFreshDiagnosticsResultAsync(unity, marker, settled, ct, note);
    }

    /// <summary>
    /// Poll compilation status until Unity reports neither compiling nor updating, surviving disconnects.
    /// Returns true when it settled, false on timeout.
    /// </summary>
    private static async Task<bool> WaitForCompilationIdleAsync(IUnityConnection unity, TimeSpan timeout, CancellationToken ct) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            try {
                var statusResponse = await unity.SendAsync("unity.editor.getCompilationStatus", null, ct);
                if (statusResponse is { Success: true, Result: not null }) {
                    var isCompiling = statusResponse.Result.Value.TryGetProperty("isCompiling", out var c) && c.GetBoolean();
                    var isUpdating = statusResponse.Result.Value.TryGetProperty("isUpdating", out var u) && u.GetBoolean();
                    if (!isCompiling && !isUpdating) return true;
                }
            } catch {
                await unity.WaitForConnectionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), ct);
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    /// <summary>
    /// Fetch error logs, keep only those stamped after <paramref name="marker"/>, parse them into structured
    /// compiler diagnostics, and render the result. Success when no fresh errors; Error otherwise.
    /// </summary>
    internal static async Task<CallToolResult> BuildFreshDiagnosticsResultAsync(IUnityConnection unity, DateTimeOffset marker, bool settled, CancellationToken ct, string? note = null) {
        var prefix = note is null ? "" : note + "\n\n";
        UnityResponse logsResponse;
        try {
            logsResponse = await unity.SendAsync("unity.debug.getLogs", new { count = 200, minLevel = "error" }, ct);
        } catch {
            return Error("Recompile triggered, but the console log could not be read to confirm the result. Check the Unity Editor manually.");
        }

        if (logsResponse is not { Success: true, Result: not null }) {
            return settled
                ? Success("Compilation completed. (Console log was unavailable, so no diagnostics could be confirmed.)")
                : Error("Compilation may still be running and diagnostics could not be read. Check the Unity Editor.");
        }

        var fresh = UnityLogRecord.Since(UnityLogRecord.ReadAll(logsResponse.Result.Value), marker);

        var diagnostics = new List<CompilerDiagnostic>();
        var otherErrors = new List<UnityLogRecord>();
        foreach (var record in fresh) {
            if (CompilerDiagnostic.TryParse(record.Message, out var diagnostic)) {
                diagnostics.Add(diagnostic);
            } else {
                otherErrors.Add(record);
            }
        }

        if (diagnostics.Count == 0 && otherErrors.Count == 0) {
            return Success(prefix + (settled
                ? "Compilation completed successfully with no errors."
                : "Compilation reported no new errors, but did not confirm idle within the timeout - verify in the Unity Editor if in doubt."));
        }

        var sb = new StringBuilder();
        sb.Append(prefix);
        var total = diagnostics.Count + otherErrors.Count;
        sb.AppendLine($"Compilation FAILED with {total} new error(s):");
        sb.AppendLine();

        foreach (var diagnostic in diagnostics) {
            sb.AppendLine(diagnostic.ToDisplayLine());
        }

        foreach (var record in otherErrors) {
            sb.AppendLine($"[{record.Level.ToUpperInvariant()}] {record.Message}");
        }

        return Error(sb.ToString());
    }

    /// <summary>
    /// Refresh the Unity Asset Database to detect external file changes, and report whether the refresh
    /// triggered a compile/import.
    /// </summary>
    [McpServerTool(Name = "refresh_assets"), Description("Refresh the Unity Asset Database to detect external file changes. NOTE: a refresh that picks up changed scripts itself triggers a recompilation + domain reload - this tool reports whether that happened but does NOT wait for it or return diagnostics. If your goal is to make code edits take effect and see compiler errors, call 'sync_and_compile' instead (it folds this refresh in). Do not chain refresh_assets then recompile - that is the footgun 'sync_and_compile' exists to prevent.")]
    public async Task<CallToolResult> RefreshAssets(
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;
        var response = await unity!.SendAsync("unity.editor.refreshAssets", null, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        // Refresh() returns after queuing an import; a triggered compile flips isCompiling/isUpdating shortly
        // after. Give it a moment, then observe so the caller knows a reload is now in flight.
        await Task.Delay(250, ct);
        var triggered = await IsCompilingOrUpdatingAsync(unity!, ct);

        return triggered switch {
            true => Success("Asset database refreshed. This triggered a recompilation/import (domain reload in progress); " +
                            "other calls to this editor may fail until it settles. Use 'sync_and_compile' to refresh, wait, and get diagnostics in one step."),
            false => Success("Asset database refreshed. No compilation or import was triggered."),
            null => Success("Asset database refreshed. (Could not determine whether a compilation was triggered.)"),
        };
    }

    /// <summary>
    /// Query the editor's compilation status. Returns true when compiling/updating, false when idle,
    /// and null when the status could not be read.
    /// </summary>
    private static async Task<bool?> IsCompilingOrUpdatingAsync(IUnityConnection unity, CancellationToken ct) {
        try {
            var statusResponse = await unity.SendAsync("unity.editor.getCompilationStatus", null, ct);
            if (statusResponse is { Success: true, Result: not null }) {
                var isCompiling = statusResponse.Result.Value.TryGetProperty("isCompiling", out var c) && c.GetBoolean();
                var isUpdating = statusResponse.Result.Value.TryGetProperty("isUpdating", out var u) && u.GetBoolean();
                return isCompiling || isUpdating;
            }
        } catch {
            // Connection may have dropped because the refresh kicked off a domain reload - that itself means
            // a compile was triggered.
            return true;
        }

        return null;
    }

    /// <summary>
    /// Invoke any managed method in the Unity process via reflection.
    /// </summary>
    [McpServerTool(Name = "invoke_managed_method"), Description("Invoke any managed method in the Unity process via reflection. Supports static/instance methods, overload disambiguation, generic args, and JSON arguments. MAIN-THREAD EXECUTION: Unity runs queued commands on the main thread, ~10 per frame, so a long-running invoke stalls every other queued request to that editor - these calls are serialized, not parallel. ASYNC METHODS: Task- and UniTask-returning methods are awaited for up to waitMs (default 2000ms); if still running, the call returns {pending: true, invocationId} immediately (unblocking the editor main thread, which is exactly what lets main-thread-bound continuations complete) - poll get_invocation_result with that invocationId for the outcome. Returned strings preserve printable characters verbatim (no unicode escaping).")]
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
        [Description("How long (ms) to wait for a Task/UniTask result before backgrounding it (default 2000, max 25000). The wait blocks the editor main thread, so keep it short for tasks that may need that thread.")] int? waitMs = null,
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

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
        if (waitMs is { } wait) parameters["waitMs"] = wait;

        var response = await unity!.SendAsync("unity.managed.invokeMethod", parameters, ct);
        if (ToErrorResult(response) is { } errorResult) return errorResult;

        if (response.Result is not { } result) {
            return Success("Method invocation succeeded with no result payload.");
        }

        return Success(JsonSerializer.Serialize(result, s_OutputJson));
    }

    /// <summary>
    /// Run Unity tests and optionally wait for completion.
    /// </summary>
    [McpServerTool(Name = "run_tests"), Description("Run Unity tests via TestRunner API. Supports filtering by mode, assemblies, test names, categories, and groups. Can wait for completion and return a summarized report.")]
    public async Task<CallToolResult> RunTests(
        [Description("Test mode: EditMode or PlayMode")] string testMode = "EditMode",
        [Description("Filter by test assembly names (without .dll)")] string[]? assemblyNames = null,
        [Description("Filter by test names. PREFIX/CLASS MATCHING: a value is matched as a prefix, so a fully-qualified class name (e.g. 'MediaVault.Tests.SlugGeneratorTests') selects every test method in that class, a namespace prefix selects everything under it, and a full method name selects that single test. Always confirm the run's echoed 'resolved filter' and matched count - a typo silently matches nothing.")] string[]? testNames = null,
        [Description("Filter by NUnit category names")] string[]? categoryNames = null,
        [Description("Filter by Unity test groups")] string[]? groupNames = null,
        [Description("Optional Unity build target name")] string? targetPlatform = null,
        [Description("Wait for completion before returning")] bool waitForCompletion = true,
        [Description("Polling interval in milliseconds when waiting")] int pollIntervalMs = 1000,
        [Description("Maximum wait time in seconds")] int timeoutSeconds = 300,
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

        var parameters = new Dictionary<string, object?> {
            ["testMode"] = testMode,
        };

        if (assemblyNames is { Length: > 0 }) parameters["assemblyNames"] = assemblyNames;
        if (testNames is { Length: > 0 }) parameters["testNames"] = testNames;
        if (categoryNames is { Length: > 0 }) parameters["categoryNames"] = categoryNames;
        if (groupNames is { Length: > 0 }) parameters["groupNames"] = groupNames;
        if (!string.IsNullOrWhiteSpace(targetPlatform)) parameters["targetPlatform"] = targetPlatform;

        var startResponse = await unity!.SendAsync("unity.editor.runTests", parameters, ct);
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

            var statusResponse = await unity!.SendAsync("unity.editor.getTestRunStatus", new { runId }, ct);
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
    [McpServerTool(Name = "get_test_run_status"), Description("Get status and results for a Unity test run. If runId is omitted, returns the LATEST run - but 'latest' is per-editor (pass the matching 'port') and is undefined after a domain reload, which discards in-memory run history; prefer passing the runId that run_tests returned. Output labels matched-and-ran vs the whole-tree discovered count and echoes the resolved filter.")]
    public async Task<CallToolResult> GetTestRunStatus(
        [Description("Optional run identifier returned by run_tests")] string? runId = null,
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

        object? parameters = string.IsNullOrWhiteSpace(runId) ? null : new { runId };
        var response = await unity!.SendAsync("unity.editor.getTestRunStatus", parameters, ct);
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
        [Description(PortParamDescription)] int? port = null,
        CancellationToken ct = default
    ) {
        var (unity, connectionError) = await ResolveConnectionAsync(port, ct);
        if (connectionError is not null) return connectionError;

        object? parameters = string.IsNullOrWhiteSpace(runId) ? null : new { runId };
        var response = await unity!.SendAsync("unity.editor.cancelTestRun", parameters, ct);
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
    [McpServerTool(Name = "list_editors"), Description("List all available Unity Editor instances that can be controlled via MCP. Each editor has its own pooled connection; pass a tool's 'port' parameter to target a specific one. Markers show which editors currently have a live pooled connection and which is the default target.")]
    public CallToolResult ListEditors() {
        var editors = EditorDiscovery.GetAvailableEditors();

        if (editors.Count == 0) {
            return Error("No Unity Editor instances found. Make sure Unity is running with the MCP plugin installed.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {editors.Count} Unity Editor instance(s):");
        sb.AppendLine();

        var defaultPort = pool.DefaultPort;

        foreach (var editor in editors) {
            var markers = new List<string>();
            if (pool.IsConnected(editor.Port)) markers.Add("CONNECTED");
            if (editor.Port == defaultPort) markers.Add("DEFAULT");
            var marker = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";

            sb.AppendLine($"  Port {editor.Port}:{marker}");
            sb.AppendLine($"    Project: {editor.ProjectName}");
            sb.AppendLine($"    Path: {editor.ProjectPath}");
            sb.AppendLine($"    PID: {editor.ProcessId}");
            if (editor.IsReloading || editor.IsCompiling) {
                var since = editor.StateChangedAt is { } at ? $" for {(DateTimeOffset.UtcNow - at).TotalSeconds:F0}s" : "";
                sb.AppendLine($"    State: {editor.State}{since}");
            }
            sb.AppendLine();
        }

        return Success(sb.ToString());
    }

    /// <summary>
    /// Select which Unity Editor instance to control.
    /// </summary>
    [McpServerTool(Name = "select_editor"), Description("Set the default Unity Editor that tool calls target when they omit a 'port'. Use 'list_editors' first to see available instances. This only changes the default and warms that editor's pooled connection; it does not disconnect any other editor, so other consumers of this bridge are unaffected. You can still target any editor per call via the tool's 'port' parameter.")]
    public async Task<CallToolResult> SelectEditor(
        [Description("The port number of the Unity Editor instance to make the default target")] int port,
        CancellationToken ct = default
    ) {
        var editor = EditorDiscovery.FindEditorByPort(port);

        if (editor is null) {
            return Error($"No Unity Editor found on port {port}. Use 'list_editors' to see available instances.");
        }

        // Set the default first so the selection sticks even if warming the connection fails.
        pool.DefaultPort = port;

        try {
            await pool.AcquireAsync(port, ct);
            return Success($"Selected Unity Editor: {editor.ProjectName} (port {port}). It is now the default target for tool calls that omit a 'port'. Other pooled editor connections are unaffected.");
        } catch (Exception ex) {
            return Error($"Selected editor on port {port} as the default, but failed to connect: {ex.Message}");
        }
    }

    // Helper methods

    /// <summary>
    /// Resolve which editor a call targets (explicit port -> default selection -> the only running
    /// editor) and return an open pooled connection to it. Each editor has its own pooled connection,
    /// so resolving one never disturbs the connections other consumers are using.
    /// </summary>
    private async Task<(IUnityConnection? Connection, CallToolResult? Error)> ResolveConnectionAsync(int? port, CancellationToken ct) {
        var resolution = PortResolver.Resolve(port, pool.DefaultPort, EditorDiscovery.GetAvailableEditors());
        if (resolution.Error is { } resolveError) {
            return (null, Error(resolveError));
        }

        var (connection, connectionError) = await EditorAvailability.AcquireAsync(pool, resolution.Port!.Value, ct);
        return connectionError is not null ? (null, Error(connectionError)) : (connection, null);
    }

    private static CallToolResult? ToErrorResult(UnityResponse response) {
        if (response.Success) return null;

        var errorText = response.Error is not null
            ? $"Unity error [{response.Error.Code}]: {response.Error.Message}"
            : "Unity command failed with unknown error";

        return Error(errorText);
    }

    internal static string FormatTestRunSummary(JsonElement result) {
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
        // 'ran' (executed) is the count the filter actually matched and ran; 'discovered' is the ENTIRE test
        // tree in the editor (all modes), which is NOT the filter-matched count - keeping them as separate
        // labelled numbers stops "5 passed" from being mistaken for "the filter matched exactly those 5".
        var summaryLine = failed > 0
            ? $"Unity tests {statusLabel}: {failed} failed, {passed} passed of {executed} ran"
            : $"Unity tests {statusLabel}: {passed} passed of {executed} ran";

        if (skipped > 0 || inconclusive > 0 || other > 0) {
            summaryLine += $" | skipped {skipped}, inconclusive {inconclusive}, other {other}";
        }

        sb.AppendLine(summaryLine);
        sb.AppendLine($"status: {status}");
        sb.AppendLine($"matched & ran: {executed}, passed: {passed}, failed: {failed}, skipped: {skipped}, inconclusive: {inconclusive}, other: {other}");
        sb.AppendLine($"discovered (entire test tree, all modes - NOT the filter-matched count): {discovered}");

        var filterLine = DescribeResolvedFilter(result);
        sb.AppendLine($"resolved filter: {filterLine}");
        if (executed == 0 && !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)) {
            sb.AppendLine("WARNING: the filter matched 0 tests - check the resolved filter above (wrong assembly/test name or mode?).");
        }

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

    /// <summary>
    /// Render the filter Unity actually applied (echoed back in the run snapshot) so the caller can confirm
    /// the run matched what they intended rather than trusting a bare pass count.
    /// </summary>
    internal static string DescribeResolvedFilter(JsonElement result) {
        if (!result.TryGetProperty("filter", out var filter) || filter.ValueKind != JsonValueKind.Object) {
            return "(none reported)";
        }

        var parts = new List<string>();

        if (filter.TryGetProperty("testMode", out var mode) && mode.ValueKind == JsonValueKind.String) {
            parts.Add($"testMode={mode.GetString()}");
        }

        // Track scoping filters (name/assembly/category/group/platform) separately from testMode: testMode
        // alone runs the whole mode, so we annotate that case rather than implying a narrow selection.
        var scopingParts = new List<string>();
        AppendArray(filter, "assemblyNames", scopingParts);
        AppendArray(filter, "testNames", scopingParts);
        AppendArray(filter, "categoryNames", scopingParts);
        AppendArray(filter, "groupNames", scopingParts);

        if (filter.TryGetProperty("targetPlatform", out var platform) && platform.ValueKind == JsonValueKind.String) {
            scopingParts.Add($"targetPlatform={platform.GetString()}");
        }

        parts.AddRange(scopingParts);

        if (scopingParts.Count == 0) {
            parts.Add("no name/assembly/category/group filter - runs everything in this mode");
        }

        return parts.Count == 0 ? "(none reported)" : string.Join(", ", parts);

        static void AppendArray(JsonElement filter, string name, List<string> parts) {
            if (!filter.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) {
                return;
            }

            var values = array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .ToList();
            if (values.Count > 0) {
                parts.Add($"{name}=[{string.Join(", ", values)}]");
            }
        }
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
