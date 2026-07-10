using System.Collections.Concurrent;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class UnityConnectionPoolTests {
    private static UnityConnectionPool CreatePool(
        ConcurrentDictionary<int, FakeUnityConnection> created,
        Func<FakeUnityConnection, bool>? connectBehavior = null
    ) {
        return new UnityConnectionPool(port => {
            var connection = new FakeUnityConnection($"ws://localhost:{port}/", connectBehavior);
            created[port] = connection;
            return connection;
        });
    }

    [Fact]
    public void GetConnection_SamePort_ReturnsSameInstance() {
        var pool = CreatePool(new ConcurrentDictionary<int, FakeUnityConnection>());

        var first = pool.GetConnection(9100);
        var second = pool.GetConnection(9100);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetConnection_DifferentPorts_ReturnsDistinctInstances() {
        var pool = CreatePool(new ConcurrentDictionary<int, FakeUnityConnection>());

        var a = pool.GetConnection(9100);
        var b = pool.GetConnection(9200);

        Assert.NotSame(a, b);
    }

    [Fact]
    public void DefaultPort_RoundTrips_IncludingNull() {
        var pool = CreatePool(new ConcurrentDictionary<int, FakeUnityConnection>());

        Assert.Null(pool.DefaultPort);

        pool.DefaultPort = 9100;
        Assert.Equal(9100, pool.DefaultPort);

        pool.DefaultPort = null;
        Assert.Null(pool.DefaultPort);
    }

    [Fact]
    public void IsConnected_ReflectsUnderlyingConnectionState() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        Assert.False(pool.IsConnected(9100)); // not created yet

        var connection = (FakeUnityConnection)pool.GetConnection(9100);
        Assert.False(pool.IsConnected(9100)); // created but not open

        connection.IsConnected = true;
        Assert.True(pool.IsConnected(9100));
    }

    [Fact]
    public async Task AcquireAsync_FirstUse_ConnectsOnce() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        var connection = (FakeUnityConnection)await pool.AcquireAsync(9100);

        Assert.True(connection.IsConnected);
        Assert.Equal(1, connection.ConnectCount);
    }

    [Fact]
    public async Task AcquireAsync_HealthyConnection_IsReusedWithoutReconnecting() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        var first = (FakeUnityConnection)await pool.AcquireAsync(9100);
        var second = (FakeUnityConnection)await pool.AcquireAsync(9100);

        Assert.Same(first, second);
        Assert.Equal(1, first.ConnectCount); // reused, not reconnected
    }

    [Fact]
    public async Task AcquireAsync_DroppedConnection_Reconnects() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        var connection = (FakeUnityConnection)await pool.AcquireAsync(9100);
        Assert.Equal(1, connection.ConnectCount);

        // Simulate a drop (e.g. domain reload).
        connection.IsConnected = false;

        var reacquired = (FakeUnityConnection)await pool.AcquireAsync(9100);

        Assert.Same(connection, reacquired);
        Assert.True(reacquired.IsConnected);
        Assert.Equal(2, reacquired.ConnectCount); // reconnected the same pooled instance
    }

    [Fact]
    public async Task AcquireAsync_DifferentPorts_DoNotDisturbEachOther() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        var a = (FakeUnityConnection)await pool.AcquireAsync(9100);
        var b = (FakeUnityConnection)await pool.AcquireAsync(9200);

        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);

        // Reconnecting b must not touch a.
        b.IsConnected = false;
        await pool.AcquireAsync(9200);

        Assert.True(a.IsConnected);
        Assert.Equal(1, a.ConnectCount);
    }

    [Fact]
    public async Task AcquireAsync_ConnectFailure_Propagates() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created, connectBehavior: _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync(9100));
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllPooledConnections() {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = CreatePool(created);

        await pool.AcquireAsync(9100);
        await pool.AcquireAsync(9200);

        await pool.DisposeAsync();

        Assert.All(created.Values, c => Assert.Equal(1, c.DisposeCount));
    }
}
