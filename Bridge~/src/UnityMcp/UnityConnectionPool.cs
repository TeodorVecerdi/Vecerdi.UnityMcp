using System.Collections.Concurrent;

namespace UnityMcp;

/// <summary>
/// Holds one lazily-created <see cref="IUnityConnection"/> per editor port so that concurrent
/// consumers of a single bridge process can each target a different editor without disturbing
/// each other's connections. Also tracks the default port used when a tool call omits one, and
/// reopens a connection the editor closes (see <see cref="ReconnectAfterLoss"/>).
/// </summary>
public sealed class UnityConnectionPool : IAsyncDisposable {
    private readonly ConcurrentDictionary<int, IUnityConnection> m_Connections = new();
    private readonly ConcurrentDictionary<int, byte> m_Reconnecting = new();
    private readonly CancellationTokenSource m_Shutdown = new();
    private readonly Func<int, IUnityConnection> m_ConnectionFactory;

    // 0 means "no default selected" - editor ports are always > 0.
    private int m_DefaultPort;

    public UnityConnectionPool(Func<int, IUnityConnection>? connectionFactory = null, TimeSpan? timeout = null) {
        m_ConnectionFactory = connectionFactory
            ?? (port => new UnityClient(EditorDiscovery.GetEditorUri(port), timeout));
    }

    /// <summary>
    /// The port targeted by tool calls that omit an explicit port. Set by <c>select_editor</c>.
    /// </summary>
    public int? DefaultPort {
        get {
            var port = Volatile.Read(ref m_DefaultPort);
            return port == 0 ? null : port;
        }
        set => Volatile.Write(ref m_DefaultPort, value ?? 0);
    }

    /// <summary>
    /// Whether a connection the editor ends is reopened in the background (default on). The editor closes every
    /// connection on each domain reload; reopening it raises <see cref="ConnectionOpened"/> once the editor is
    /// back, so dynamic tool discovery follows the reload instead of waiting for the next tool call.
    /// </summary>
    public bool ReconnectAfterLoss { get; set; } = true;

    /// <summary>Get (or lazily create) the pooled connection for a port. Does not open it.</summary>
    public IUnityConnection GetConnection(int port) => m_Connections.GetOrAdd(port, p => {
        var connection = m_ConnectionFactory(p);
        connection.Connected += () => ConnectionOpened?.Invoke(p, connection);
        // Off the thread that raised it: that is the connection's own receive loop, still finishing.
        connection.ConnectionLost += () => _ = Task.Run(() => ReconnectAsync(p));
        return connection;
    });

    /// <summary>Whether a connection for <paramref name="port"/> exists in the pool and is open.</summary>
    public bool IsConnected(int port) => m_Connections.TryGetValue(port, out var connection) && connection.IsConnected;

    /// <summary>
    /// Raised after a pooled connection (re)opens, forwarded from the connection's own
    /// <see cref="IUnityConnection.Connected"/> event — so it fires on EVERY successful
    /// connect, including the internal reconnects inside WaitForConnectionAsync that follow a
    /// domain reload (exactly when the editor's dynamic tool set may have changed), not just
    /// connects that happen to go through <see cref="AcquireAsync"/>.
    /// </summary>
    public event Action<int, IUnityConnection>? ConnectionOpened;

    /// <summary>
    /// Get the pooled connection for a port, opening it if it is not currently connected.
    /// Reuses healthy connections and reconnects dropped ones, isolated per port.
    /// </summary>
    public async Task<IUnityConnection> AcquireAsync(int port, CancellationToken ct = default) {
        var connection = GetConnection(port);
        if (!connection.IsConnected) {
            await connection.ConnectAsync(ct);
        }

        return connection;
    }

    /// <summary>
    /// Reopen the connection to <paramref name="port"/> after the editor ended it, waiting out a domain reload the
    /// way a tool call would (<see cref="EditorAvailability.AcquireAsync"/>): it gives up when the editor is gone, its
    /// MCP server stays unreachable, or the reload outlasts the wait. Giving up costs nothing, since the next tool
    /// call reconnects anyway. One attempt per port at a time; a tool call reconnecting meanwhile is harmless, as
    /// connecting an open connection is a no-op.
    /// </summary>
    private async Task ReconnectAsync(int port) {
        if (!ReconnectAfterLoss || m_Shutdown.IsCancellationRequested || !m_Reconnecting.TryAdd(port, 0)) {
            return;
        }

        try {
            await EditorAvailability.AcquireAsync(this, port, m_Shutdown.Token);
        } catch (Exception) {
            // Cancelled by disposal, or failed outright: either way the next tool call reconnects on its own.
        } finally {
            m_Reconnecting.TryRemove(port, out _);
        }
    }

    public async ValueTask DisposeAsync() {
        await m_Shutdown.CancelAsync();
        foreach (var connection in m_Connections.Values) {
            await connection.DisposeAsync();
        }

        m_Connections.Clear();
    }
}
