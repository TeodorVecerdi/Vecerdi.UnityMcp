using System.Collections.Concurrent;

namespace UnityMcp;

/// <summary>
/// Holds one lazily-created <see cref="IUnityConnection"/> per editor port so that concurrent
/// consumers of a single bridge process can each target a different editor without disturbing
/// each other's connections. Also tracks the default port used when a tool call omits one.
/// </summary>
public sealed class UnityConnectionPool : IAsyncDisposable {
    private readonly ConcurrentDictionary<int, IUnityConnection> m_Connections = new();
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

    /// <summary>Get (or lazily create) the pooled connection for a port. Does not open it.</summary>
    public IUnityConnection GetConnection(int port) => m_Connections.GetOrAdd(port, m_ConnectionFactory);

    /// <summary>Whether a connection for <paramref name="port"/> exists in the pool and is open.</summary>
    public bool IsConnected(int port) => m_Connections.TryGetValue(port, out var connection) && connection.IsConnected;

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

    public async ValueTask DisposeAsync() {
        foreach (var connection in m_Connections.Values) {
            await connection.DisposeAsync();
        }

        m_Connections.Clear();
    }
}
