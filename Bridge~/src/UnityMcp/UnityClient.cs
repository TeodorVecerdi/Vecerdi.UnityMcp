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
public sealed class UnityClient : IAsyncDisposable {
    private readonly string m_Uri;
    private readonly TimeSpan m_Timeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UnityResponse>> m_PendingRequests = new();
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

    public UnityClient(string uri = "ws://localhost:9999/", TimeSpan? timeout = null) {
        m_Uri = uri;
        m_Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        if (IsConnected) return;

        m_WebSocket?.Dispose();
        m_WebSocket = new ClientWebSocket();

        await m_WebSocket.ConnectAsync(new Uri(m_Uri), ct);

        m_ReceiveCts = new CancellationTokenSource();
        m_ReceiveTask = ReceiveLoopAsync(m_ReceiveCts.Token);
    }

    public async Task DisconnectAsync() {
        if (m_WebSocket is null) return;

        m_ReceiveCts?.Cancel();

        if (m_WebSocket.State == WebSocketState.Open) {
            try {
                await m_WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            } catch {
                // Ignore close errors
            }
        }

        if (m_ReceiveTask is not null) {
            try {
                await m_ReceiveTask;
            } catch {
                // Ignore receive loop errors
            }
        }

        m_WebSocket.Dispose();
        m_WebSocket = null;
        m_ReceiveCts?.Dispose();
        m_ReceiveCts = null;
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
        if (!IsConnected) {
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
                await m_WebSocket!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            } finally {
                m_SendLock.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(m_Timeout);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != tcs.Task) {
                throw new TimeoutException($"Request '{command}' timed out after {m_Timeout.TotalSeconds}s");
            }

            return await tcs.Task;
        } finally {
            m_PendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct) {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested && m_WebSocket?.State == WebSocketState.Open) {
            try {
                var result = await m_WebSocket.ReceiveAsync(buffer, ct);

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
        m_SendLock.Dispose();
    }
}
