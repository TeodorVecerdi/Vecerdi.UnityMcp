using System.Text.Json;
using UnityMcp;
using UnityMcp.Mcp;

// Configuration
var unityUri = Environment.GetEnvironmentVariable("UNITY_MCP_URI") ?? "ws://localhost:9999/";
var logPath = Environment.GetEnvironmentVariable("UNITY_MCP_LOG");

// Set up logging (to file if specified, otherwise discard)
await using var logWriter = !string.IsNullOrEmpty(logPath)
    ? new StreamWriter(logPath, append: true) { AutoFlush = true }
    : TextWriter.Null;

await logWriter.WriteLineAsync($"[{DateTime.Now:s}] Unity MCP Server starting...");
await logWriter.WriteLineAsync($"[{DateTime.Now:s}] Unity URI: {unityUri}");

// Initialize components
await using var transport = new StdioTransport(log: logWriter);
await using var unityClient = new UnityClient(unityUri);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    cts.Cancel();
};

// Main message loop
try {
    while (!cts.Token.IsCancellationRequested) {
        var request = await transport.ReadRequestAsync(cts.Token);

        if (request is null) {
            // EOF or parse error
            if (Console.IsInputRedirected) {
                // stdin closed, exit gracefully
                break;
            }
            continue;
        }

        var response = await HandleRequestAsync(request, unityClient, logWriter, cts.Token);

        if (response is not null) {
            await transport.WriteResponseAsync(response, cts.Token);
        }
    }
} catch (OperationCanceledException) {
    // Expected on shutdown
}

await logWriter.WriteLineAsync($"[{DateTime.Now:s}] Unity MCP Server shutting down.");
return 0;

// Request handlers
static async Task<JsonRpcResponse?> HandleRequestAsync(
    JsonRpcRequest request,
    UnityClient unityClient,
    TextWriter log,
    CancellationToken ct
) {
    try {
        return request.Method switch {
            "initialize" => HandleInitialize(request),
            "initialized" => null, // Notification, no response
            "ping" => HandlePing(request),
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolsCallAsync(request, unityClient, log, ct),
            _ => JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.MethodNotFound,
                $"Unknown method: {request.Method}"),
        };
    } catch (Exception ex) {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Error handling {request.Method}: {ex}");
        return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InternalError, ex.Message);
    }
}

static JsonRpcResponse HandleInitialize(JsonRpcRequest request) {
    var result = new InitializeResult {
        ProtocolVersion = "2024-11-05",
        Capabilities = new ServerCapabilities {
            Tools = new ToolsCapability { ListChanged = false },
        },
        ServerInfo = new ServerInfo {
            Name = "unity-mcp",
            Version = "1.0.0",
        },
    };

    return JsonRpcResponse.Success(request.Id, result);
}

static JsonRpcResponse HandlePing(JsonRpcRequest request) {
    return JsonRpcResponse.Success(request.Id, new { });
}

static JsonRpcResponse HandleToolsList(JsonRpcRequest request) {
    var result = new ToolsListResult {
        Tools = UnityTools.All.Select(t => t.ToDefinition()).ToList(),
    };

    return JsonRpcResponse.Success(request.Id, result);
}

static async Task<JsonRpcResponse> HandleToolsCallAsync(
    JsonRpcRequest request,
    UnityClient unityClient,
    TextWriter log,
    CancellationToken ct
) {
    // Parse params
    ToolCallParams? callParams = null;
    if (request.Params is { } p) {
        try {
            callParams = p.Deserialize<ToolCallParams>();
        } catch {
            return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InvalidParams,
                "Invalid tool call parameters");
        }
    }

    if (callParams is null || string.IsNullOrEmpty(callParams.Name)) {
        return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InvalidParams,
            "Tool name is required");
    }

    // Find the tool
    var tool = UnityTools.GetByName(callParams.Name);
    if (tool is null) {
        return JsonRpcResponse.Failure(request.Id, JsonRpcErrorCodes.InvalidParams,
            $"Unknown tool: {callParams.Name}");
    }

    // Ensure connected to Unity
    if (!unityClient.IsConnected) {
        try {
            await log.WriteLineAsync($"[{DateTime.Now:s}] Connecting to Unity...");
            await unityClient.ConnectAsync(ct);
            await log.WriteLineAsync($"[{DateTime.Now:s}] Connected to Unity.");
        } catch (Exception ex) {
            await log.WriteLineAsync($"[{DateTime.Now:s}] Failed to connect to Unity: {ex.Message}");

            var errorResult = new ToolCallResult {
                IsError = true,
                Content = [ContentBlock.Text($"Failed to connect to Unity Editor at {unityClient}: {ex.Message}\n\nMake sure Unity Editor is running and the MCP plugin is active.")],
            };
            return JsonRpcResponse.Success(request.Id, errorResult);
        }
    }

    // Special handling for recompile - blocks until compilation completes
    if (callParams.Name == "unity_recompile") {
        return await HandleRecompileAsync(request, unityClient, log, ct);
    }

    // Transform parameters if needed
    var unityParams = tool.TransformParams?.Invoke(callParams.Arguments) ?? callParams.Arguments;

    // Send command to Unity
    try {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Calling Unity: {tool.UnityCommand}");
        var response = await unityClient.SendAsync(tool.UnityCommand, unityParams, ct);

        if (!response.Success) {
            var errorText = response.Error is not null
                ? $"Unity error [{response.Error.Code}]: {response.Error.Message}"
                : "Unity command failed with unknown error";

            var errorResult = new ToolCallResult {
                IsError = true,
                Content = [ContentBlock.Text(errorText)],
            };
            return JsonRpcResponse.Success(request.Id, errorResult);
        }

        // Format the response
        var text = tool.FormatResponse?.Invoke(response.Result)
            ?? (response.Result.HasValue
                ? JsonSerializer.Serialize(response.Result.Value, new JsonSerializerOptions { WriteIndented = true })
                : "Command completed successfully.");

        var successResult = new ToolCallResult {
            Content = [ContentBlock.Text(text)],
        };
        return JsonRpcResponse.Success(request.Id, successResult);

    } catch (TimeoutException) {
        var timeoutResult = new ToolCallResult {
            IsError = true,
            Content = [ContentBlock.Text("Request to Unity timed out. The Editor may be busy or unresponsive.")],
        };
        return JsonRpcResponse.Success(request.Id, timeoutResult);
    } catch (Exception ex) {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Error calling Unity: {ex}");

        var errorResult = new ToolCallResult {
            IsError = true,
            Content = [ContentBlock.Text($"Error communicating with Unity: {ex.Message}")],
        };
        return JsonRpcResponse.Success(request.Id, errorResult);
    }
}

/// <summary>
/// Handle unity_recompile with blocking behavior:
/// 1. Send recompile command
/// 2. Wait for Unity to reconnect after domain reload
/// 3. Check compilation status
/// 4. Return errors if any, otherwise success
/// </summary>
static async Task<JsonRpcResponse> HandleRecompileAsync(
    JsonRpcRequest request,
    UnityClient unityClient,
    TextWriter log,
    CancellationToken ct
) {
    await log.WriteLineAsync($"[{DateTime.Now:s}] Starting blocking recompile...");

    // Step 1: Send recompile command
    try {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Sending recompile command...");
        var recompileResponse = await unityClient.SendAsync("unity.editor.recompile", null, ct);

        if (!recompileResponse.Success) {
            var errorText = recompileResponse.Error is not null
                ? $"Unity error [{recompileResponse.Error.Code}]: {recompileResponse.Error.Message}"
                : "Recompile command failed";

            return JsonRpcResponse.Success(request.Id, new ToolCallResult {
                IsError = true,
                Content = [ContentBlock.Text(errorText)],
            });
        }
    } catch (Exception ex) {
        // This is expected - the connection may drop immediately due to domain reload
        await log.WriteLineAsync($"[{DateTime.Now:s}] Recompile command sent (connection may have dropped: {ex.Message})");
    }

    // Step 2: Wait for Unity to come back after domain reload
    // Domain reload typically takes 2-15 seconds depending on project size
    await log.WriteLineAsync($"[{DateTime.Now:s}] Waiting for Unity to reconnect after domain reload...");

    // First, give Unity a moment to start the domain reload
    await Task.Delay(1000, ct);

    var reconnected = await unityClient.WaitForConnectionAsync(
        timeout: TimeSpan.FromSeconds(60),
        pollInterval: TimeSpan.FromMilliseconds(500),
        ct
    );

    if (!reconnected) {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Timed out waiting for Unity to reconnect.");
        return JsonRpcResponse.Success(request.Id, new ToolCallResult {
            IsError = true,
            Content = [ContentBlock.Text("Timed out waiting for Unity to reconnect after recompile. The Editor may still be compiling or may have encountered a fatal error.")],
        });
    }

    await log.WriteLineAsync($"[{DateTime.Now:s}] Unity reconnected. Checking compilation status...");

    // Step 3: Wait a moment for Unity to settle, then check if still compiling
    await Task.Delay(500, ct);

    // Poll until compilation is complete (in case we reconnected while still compiling)
    var compilationTimeout = DateTime.UtcNow + TimeSpan.FromSeconds(120);
    while (DateTime.UtcNow < compilationTimeout && !ct.IsCancellationRequested) {
        try {
            var statusResponse = await unityClient.SendAsync("unity.editor.getCompilationStatus", null, ct);
            if (statusResponse.Success && statusResponse.Result.HasValue) {
                var isCompiling = statusResponse.Result.Value.TryGetProperty("isCompiling", out var c) && c.GetBoolean();
                var isUpdating = statusResponse.Result.Value.TryGetProperty("isUpdating", out var u) && u.GetBoolean();

                if (!isCompiling && !isUpdating) {
                    await log.WriteLineAsync($"[{DateTime.Now:s}] Compilation complete.");
                    break;
                }

                await log.WriteLineAsync($"[{DateTime.Now:s}] Still compiling/updating, waiting...");
            }
        } catch {
            // Connection may have dropped again, try to reconnect
            await unityClient.WaitForConnectionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), ct);
        }

        await Task.Delay(1000, ct);
    }

    // Step 4: Check for compilation errors in logs
    try {
        var logsResponse = await unityClient.SendAsync("unity.debug.getLogs", new { count = 100, minLevel = "error" }, ct);

        if (logsResponse.Success && logsResponse.Result.HasValue &&
            logsResponse.Result.Value.TryGetProperty("logs", out var logs) &&
            logs.ValueKind == JsonValueKind.Array &&
            logs.GetArrayLength() > 0) {

            // There are errors - format them nicely
            var sb = new System.Text.StringBuilder();
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

            await log.WriteLineAsync($"[{DateTime.Now:s}] Compilation had {logs.GetArrayLength()} error(s).");

            return JsonRpcResponse.Success(request.Id, new ToolCallResult {
                IsError = true,
                Content = [ContentBlock.Text(sb.ToString())],
            });
        }

        // No errors - success!
        await log.WriteLineAsync($"[{DateTime.Now:s}] Compilation succeeded with no errors.");
        return JsonRpcResponse.Success(request.Id, new ToolCallResult {
            Content = [ContentBlock.Text("Compilation completed successfully with no errors.")],
        });

    } catch (Exception ex) {
        await log.WriteLineAsync($"[{DateTime.Now:s}] Error checking logs: {ex.Message}");

        return JsonRpcResponse.Success(request.Id, new ToolCallResult {
            Content = [ContentBlock.Text("Recompile triggered. Unable to verify completion status - check Unity Editor manually.")],
        });
    }
}
