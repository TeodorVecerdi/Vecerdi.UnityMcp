using UnityMcp;

namespace UnityMcp.Tests;

/// <summary>
/// In-memory <see cref="IUnityConnection"/> used to exercise the pool's connect/reuse/reconnect
/// behavior without a real WebSocket or Unity editor.
/// </summary>
internal sealed class FakeUnityConnection : IUnityConnection {
    private readonly Func<FakeUnityConnection, bool>? m_ConnectBehavior;

    public FakeUnityConnection(string uri, Func<FakeUnityConnection, bool>? connectBehavior = null) {
        CurrentUri = uri;
        m_ConnectBehavior = connectBehavior;
    }

    public event Action? Connected;
    public bool IsConnected { get; set; }
    public string CurrentUri { get; }
    public int ConnectCount { get; private set; }
    public int DisposeCount { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) {
        ConnectCount++;

        // Default behavior: connecting succeeds and marks the connection open.
        var succeeded = m_ConnectBehavior?.Invoke(this) ?? true;
        if (succeeded) {
            IsConnected = true;
            Connected?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync() {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<bool> WaitForConnectionAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct = default) {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task<UnityResponse> SendAsync(string command, object? parameters = null, CancellationToken ct = default) {
        if (!IsConnected) {
            throw new InvalidOperationException("Not connected.");
        }

        return Task.FromResult(new UnityResponse { Id = "1", Success = true });
    }

    public ValueTask DisposeAsync() {
        DisposeCount++;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
