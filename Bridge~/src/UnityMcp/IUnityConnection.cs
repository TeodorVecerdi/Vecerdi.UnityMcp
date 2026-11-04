namespace UnityMcp;

/// <summary>
/// A single connection to one Unity Editor instance. Abstracted from <see cref="UnityClient"/>
/// so the connection pool can be exercised with fake connections in tests.
/// </summary>
public interface IUnityConnection : IAsyncDisposable {
    /// <summary>
    /// Raised after every successful connect — including reconnects performed inside
    /// <see cref="WaitForConnectionAsync"/> (e.g. surviving a domain reload). Dynamic tool
    /// discovery keys off this: a fresh connection is the moment the editor's tool set may
    /// have changed.
    /// </summary>
    event Action? Connected;

    /// <summary>
    /// Raised when the editor's side ends an open connection: it closed the socket (which it does to every
    /// connection on each domain reload) or the socket broke. Not raised when the bridge disconnects on purpose
    /// (<see cref="DisconnectAsync"/>, the reconnect loop in <see cref="WaitForConnectionAsync"/>, disposal).
    /// May be raised on a background thread.
    /// </summary>
    event Action? ConnectionLost;

    /// <summary>Whether the underlying transport is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>The editor URI this connection targets.</summary>
    string CurrentUri { get; }

    /// <summary>Open the connection. No-op if already connected.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Close the connection if open.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Wait for the editor to become available, reconnecting as needed. Used to survive
    /// domain reloads where the connection drops mid-operation.
    /// </summary>
    Task<bool> WaitForConnectionAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct = default);

    /// <summary>Send a command and await its correlated response.</summary>
    Task<UnityResponse> SendAsync(string command, object? parameters = null, CancellationToken ct = default);
}
