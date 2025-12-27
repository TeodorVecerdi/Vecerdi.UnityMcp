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
