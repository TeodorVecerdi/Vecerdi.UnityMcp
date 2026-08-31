using System.Collections.Concurrent;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

/// <summary>
/// The wait-through-a-domain-reload behaviour behind every tool's connection resolution. These tests share the
/// static knobs on <see cref="EditorAvailability"/>, so they live in one class (xUnit runs a class serially) and
/// restore the defaults afterwards.
/// </summary>
public sealed class EditorAvailabilityTests : IDisposable {
    private const int Port = 9100;

    private readonly Func<int, EditorInstance?> m_OriginalFindEditor = EditorAvailability.FindEditor;
    private readonly TimeSpan m_OriginalWait = EditorAvailability.ReloadWait;
    private readonly TimeSpan m_OriginalPoll = EditorAvailability.PollInterval;

    public EditorAvailabilityTests() {
        EditorAvailability.PollInterval = TimeSpan.FromMilliseconds(10);
        EditorAvailability.ReloadWait = TimeSpan.FromSeconds(5);
    }

    public void Dispose() {
        EditorAvailability.FindEditor = m_OriginalFindEditor;
        EditorAvailability.ReloadWait = m_OriginalWait;
        EditorAvailability.PollInterval = m_OriginalPoll;
    }

    private static EditorInstance Editor(string state, DateTimeOffset? changedAt = null) => new() {
        Port = Port,
        ProjectName = "MediaVault",
        ProjectPath = "D:/x/MediaVault",
        ProcessId = Environment.ProcessId,
        State = state,
        StateChangedAt = changedAt ?? DateTimeOffset.UtcNow,
    };

    /// <summary>A pool whose single fake connection only connects while <paramref name="serverUp"/> says so.</summary>
    private static (UnityConnectionPool Pool, ConcurrentDictionary<int, FakeUnityConnection> Created) PoolGatedBy(Func<bool> serverUp) {
        var created = new ConcurrentDictionary<int, FakeUnityConnection>();
        var pool = new UnityConnectionPool(port => {
            var connection = new FakeUnityConnection($"ws://localhost:{port}/", _ => serverUp() ? true : throw new InvalidOperationException("connection refused"));
            created[port] = connection;
            return connection;
        });
        return (pool, created);
    }

    [Fact]
    public async Task ServerUp_ConnectsWithoutConsultingRegistry() {
        var probes = 0;
        EditorAvailability.FindEditor = _ => { probes++; return Editor(EditorInstanceState.Ready); };
        var (pool, _) = PoolGatedBy(() => true);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(error);
        Assert.True(connection!.IsConnected);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task ReloadingEditor_WaitsUntilReadyThenConnects() {
        // Registry: reloading for the first three reads, then ready; the server comes up together with 'ready'.
        var reads = 0;
        var serverUp = false;
        EditorAvailability.FindEditor = _ => {
            reads++;
            if (reads <= 3) return Editor(EditorInstanceState.Reloading);
            serverUp = true;
            return Editor(EditorInstanceState.Ready);
        };
        var (pool, created) = PoolGatedBy(() => serverUp);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(error);
        Assert.True(connection!.IsConnected);
        Assert.True(reads >= 4);
        Assert.True(created[Port].ConnectCount >= 2, "should have retried the connect after the reload ended");
    }

    [Fact]
    public async Task ReadyThenReloadingAMomentLater_StillWaits() {
        // The socket drops a beat before the entry flips to 'reloading' (compile finished cleanly, reload imminent).
        var reads = 0;
        var serverUp = false;
        EditorAvailability.FindEditor = _ => {
            reads++;
            return reads switch {
                1 => Editor(EditorInstanceState.Ready),
                <= 3 => Editor(EditorInstanceState.Reloading),
                _ => Tick(),
            };

            EditorInstance Tick() { serverUp = true; return Editor(EditorInstanceState.Ready); }
        };
        var (pool, _) = PoolGatedBy(() => serverUp);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(error);
        Assert.True(connection!.IsConnected);
    }

    [Fact]
    public async Task ReloadThatNeverEnds_TimesOutNamingTheReload() {
        EditorAvailability.ReloadWait = TimeSpan.FromMilliseconds(60);
        EditorAvailability.FindEditor = _ => Editor(EditorInstanceState.Reloading, DateTimeOffset.UtcNow.AddSeconds(-42));
        var (pool, _) = PoolGatedBy(() => false);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(connection);
        Assert.Contains("reloading the script domain", error);
        Assert.Contains("42s", error);
        Assert.DoesNotContain("Make sure the Editor is running", error);
    }

    [Fact]
    public async Task NoRegistryEntry_ReportsEditorClosed() {
        EditorAvailability.FindEditor = _ => null;
        var (pool, _) = PoolGatedBy(() => false);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(connection);
        Assert.Contains("probably been closed", error);
    }

    [Fact]
    public async Task EditorClosedMidReload_ReportsClosedInsteadOfWaitingOut() {
        EditorAvailability.ReloadWait = TimeSpan.FromSeconds(30);
        var reads = 0;
        EditorAvailability.FindEditor = _ => ++reads <= 2 ? Editor(EditorInstanceState.Reloading) : null;
        var (pool, _) = PoolGatedBy(() => false);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(connection);
        Assert.Contains("closed while reloading", error);
    }

    [Fact]
    public async Task AliveButUnreachable_ReportsPluginProblemWithState() {
        EditorAvailability.FindEditor = _ => Editor(EditorInstanceState.Ready);
        var (pool, _) = PoolGatedBy(() => false);

        var (connection, error) = await EditorAvailability.AcquireAsync(pool, Port, CancellationToken.None);

        Assert.Null(connection);
        Assert.Contains("connection refused", error);
        Assert.Contains("state 'ready'", error);
        Assert.Contains("MCP plugin", error);
    }
}
