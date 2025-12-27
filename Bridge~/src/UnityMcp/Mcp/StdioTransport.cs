using System.Text.Json;

namespace UnityMcp.Mcp;

/// <summary>
/// Handles MCP communication over stdio using JSON-RPC.
/// Messages are newline-delimited JSON.
/// </summary>
public sealed class StdioTransport : IAsyncDisposable {
    private readonly TextReader m_Input;
    private readonly TextWriter m_Output;
    private readonly TextWriter m_Log;
    private readonly SemaphoreSlim m_WriteLock = new(1, 1);

    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public StdioTransport(TextReader? input = null, TextWriter? output = null, TextWriter? log = null) {
        m_Input = input ?? Console.In;
        m_Output = output ?? Console.Out;
        m_Log = log ?? TextWriter.Null;
    }

    /// <summary>
    /// Read the next JSON-RPC request from stdin.
    /// </summary>
    public async Task<JsonRpcRequest?> ReadRequestAsync(CancellationToken ct = default) {
        var line = await m_Input.ReadLineAsync(ct);

        if (string.IsNullOrEmpty(line)) {
            return null;
        }

        await m_Log.WriteLineAsync($"[MCP] <- {line}");

        try {
            return JsonSerializer.Deserialize<JsonRpcRequest>(line, s_JsonOptions);
        } catch (JsonException ex) {
            await m_Log.WriteLineAsync($"[MCP] Parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Write a JSON-RPC response to stdout.
    /// </summary>
    public async Task WriteResponseAsync(JsonRpcResponse response, CancellationToken ct = default) {
        var json = JsonSerializer.Serialize(response, s_JsonOptions);

        await m_WriteLock.WaitAsync(ct);
        try {
            await m_Log.WriteLineAsync($"[MCP] -> {json}");
            await m_Output.WriteLineAsync(json);
            await m_Output.FlushAsync(ct);
        } finally {
            m_WriteLock.Release();
        }
    }

    /// <summary>
    /// Send a JSON-RPC notification (no id, no response expected).
    /// </summary>
    public async Task SendNotificationAsync(string method, object? parameters = null, CancellationToken ct = default) {
        var notification = new {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        };

        var json = JsonSerializer.Serialize(notification, s_JsonOptions);

        await m_WriteLock.WaitAsync(ct);
        try {
            await m_Log.WriteLineAsync($"[MCP] -> {json}");
            await m_Output.WriteLineAsync(json);
            await m_Output.FlushAsync(ct);
        } finally {
            m_WriteLock.Release();
        }
    }

    public ValueTask DisposeAsync() {
        m_WriteLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
