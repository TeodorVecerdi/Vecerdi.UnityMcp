using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UnityMcp;

/// <summary>
/// Request to send to Unity.
/// </summary>
public sealed class UnityRequest {
    public string Id { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public object? Params { get; set; }
}

/// <summary>
/// Response from Unity.
/// </summary>
public sealed class UnityResponse {
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public JsonElement? Result { get; set; }
    public UnityError? Error { get; set; }
}

public sealed class UnityError {
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }
}

/// <summary>
/// WebSocket client for communicating with Unity Editor's MCP plugin.
/// </summary>
/// <summary>Thrown when the editor drops the socket while a request is waiting for its answer.</summary>
public sealed class UnityConnectionLostException(string message) : Exception(message);

public sealed class UnityClient : IUnityConnection {
    private readonly string m_Uri;
    private readonly TimeSpan m_Timeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UnityResponse>> m_PendingRequests = new();
    private readonly SemaphoreSlim m_LifecycleLock = new(1, 1);
    private readonly SemaphoreSlim m_SendLock = new(1, 1);

    private ClientWebSocket? m_WebSocket;
    private CancellationTokenSource? m_ReceiveCts;
    private Task? m_ReceiveTask;
    private int m_RequestId;

    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool IsConnected => m_WebSocket?.State == WebSocketState.Open;
    public string CurrentUri => m_Uri;

    public UnityClient(string? uri = null, TimeSpan? timeout = null) {
        m_Uri = uri ?? EditorDiscovery.GetDefaultEditorUri();
        m_Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public event Action? Connected;

    public async Task ConnectAsync(CancellationToken ct = default) {
        var opened = false;
        await m_LifecycleLock.WaitAsync(ct);
        try {
            if (m_WebSocket?.State == WebSocketState.Open) return;

            await DisconnectCoreAsync();

            ClientWebSocket? webSocket = null;
            CancellationTokenSource? receiveCts = null;

            try {
                webSocket = new ClientWebSocket();
                await webSocket.ConnectAsync(new Uri(m_Uri), ct);

                receiveCts = new CancellationTokenSource();
                var receiveTask = ReceiveLoopAsync(webSocket, receiveCts.Token);

                m_WebSocket = webSocket;
                m_ReceiveCts = receiveCts;
                m_ReceiveTask = receiveTask;

                webSocket = null;
                receiveCts = null;
                opened = true;
            } finally {
                webSocket?.Dispose();
                receiveCts?.Dispose();
            }
        } finally {
            m_LifecycleLock.Release();
        }

        // Raised outside the lifecycle lock: handlers may immediately send on this connection.
        if (opened) {
            Connected?.Invoke();
        }
    }

    public async Task DisconnectAsync() {
        await m_LifecycleLock.WaitAsync();
        try {
            await DisconnectCoreAsync();
        } finally {
            m_LifecycleLock.Release();
        }
    }

    /// <summary>
    /// Wait for Unity to become available, reconnecting as needed.
    /// </summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct = default) {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) {
            try {
                // Force a fresh connection attempt
                await DisconnectAsync();
                await ConnectAsync(ct);

                if (IsConnected) {
                    return true;
                }
            } catch {
                // Connection failed, wait and retry
            }

            await Task.Delay(pollInterval, ct);
        }

        return false;
    }

    public async Task<UnityResponse> SendAsync(string command, object? parameters = null, CancellationToken ct = default) {
        await m_LifecycleLock.WaitAsync(ct);
        try {
            var webSocket = m_WebSocket;
            if (webSocket?.State != WebSocketState.Open) {
                throw new InvalidOperationException("Not connected to Unity. Call ConnectAsync first.");
            }

            var requestId = Interlocked.Increment(ref m_RequestId).ToString();
            var request = new UnityRequest {
                Id = requestId,
                Command = command,
                Params = parameters,
            };

            var tcs = new TaskCompletionSource<UnityResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            m_PendingRequests[requestId] = tcs;

            try {
                var json = JsonSerializer.Serialize(request, s_JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);

                await m_SendLock.WaitAsync(ct);
                try {
                    await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                } finally {
                    m_SendLock.Release();
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(m_Timeout);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

                if (completedTask != tcs.Task) {
                    throw new TimeoutException($"Request '{command}' timed out after {m_Timeout.TotalSeconds}s");
                }

                try {
                    return await tcs.Task;
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    // The receive loop ended before an answer came: the editor closed the socket under us, which in
                    // normal use means a domain reload started. Say so instead of "A task was canceled."
                    var port = EditorDiscovery.TryGetPort(m_Uri);
                    var why = port is { } p ? EditorDiscovery.DescribeInterruption(p) : null;
                    throw new UnityConnectionLostException(
                        $"The connection to Unity closed while waiting for '{command}'" +
                        (why is not null ? $": {why}." : " - the editor is most likely reloading the script domain.") +
                        " The command may or may not have run; check the editor state before repeating a non-idempotent call.");
                }
            } finally {
                m_PendingRequests.TryRemove(requestId, out _);
            }
        } finally {
            m_LifecycleLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken ct) {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open) {
            try {
                var result = await webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close) {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text) {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage) {
                        var message = messageBuilder.ToString();
                        messageBuilder.Clear();

                        try {
                            var response = JsonSerializer.Deserialize<UnityResponse>(message, s_JsonOptions);
                            if (response is not null && m_PendingRequests.TryRemove(response.Id, out var tcs)) {
                                tcs.TrySetResult(response);
                            }
                        } catch (JsonException) {
                            // Ignore malformed responses
                        }
                    }
                }
            } catch (OperationCanceledException) {
                break;
            } catch (WebSocketException) {
                break;
            }
        }

        // Complete all pending requests with cancellation
        foreach (var (_, tcs) in m_PendingRequests) {
            tcs.TrySetCanceled();
        }
        m_PendingRequests.Clear();
    }

    public async ValueTask DisposeAsync() {
        await DisconnectAsync();
        m_LifecycleLock.Dispose();
        m_SendLock.Dispose();
    }

    private async Task DisconnectCoreAsync() {
        var webSocket = m_WebSocket;
        var receiveCts = m_ReceiveCts;
        var receiveTask = m_ReceiveTask;

        m_WebSocket = null;
        m_ReceiveCts = null;
        m_ReceiveTask = null;

        if (webSocket is null) return;

        receiveCts?.Cancel();

        try {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived) {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
        } catch {
            // Ignore close errors
        } finally {
            if (receiveTask is not null) {
                try {
                    await receiveTask;
                } catch {
                    // Ignore receive loop errors
                }
            }

            webSocket.Dispose();
            receiveCts?.Dispose();
        }
    }
}
