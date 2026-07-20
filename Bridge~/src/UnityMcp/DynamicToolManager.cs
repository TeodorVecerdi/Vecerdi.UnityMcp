using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace UnityMcp;

/// <summary>
/// Discovers agent-facing tools from Unity editors at runtime (todo #243). Whenever a pooled
/// editor connection (re)opens — including after domain reloads, when the editor's tool set may
/// have changed — this fetches <c>unity.meta.listTools</c> and reconciles the results into the
/// server's tool collection as <see cref="UnityProxyTool"/>s. The SDK raises
/// <c>notifications/tools/list_changed</c> off collection changes, so clients re-query on their
/// own. Native (attributed) tools always win name collisions: dynamic registration skips names
/// that already exist, which lets bridge-side tools shadow editor-advertised ones during
/// migration. Editors without the listTools command simply contribute nothing.
/// </summary>
public sealed class DynamicToolManager(UnityConnectionPool pool, IServiceProvider services, ILogger<DynamicToolManager> logger) {
    private readonly SemaphoreSlim m_RefreshLock = new(1, 1);
    private readonly Dictionary<string, UnityProxyTool> m_DynamicTools = new(StringComparer.Ordinal);

    /// <summary>Subscribe to pool connection events. Call once at startup.</summary>
    public void Attach() {
        pool.ConnectionOpened += (port, connection) => _ = RefreshSafeAsync(connection);

        // Warm the obvious editor (selected default, or the only one running) so its tools are
        // discovered near session start instead of after the first tool call. Best-effort: no
        // editor running just means discovery waits for the first real connection.
        _ = WarmInitialConnectionAsync();
    }

    private async Task WarmInitialConnectionAsync() {
        try {
            var resolution = PortResolver.Resolve(null, pool.DefaultPort, EditorDiscovery.GetAvailableEditors());
            if (resolution.Port is { } port) {
                await pool.AcquireAsync(port, CancellationToken.None);
            }
        } catch (Exception ex) {
            logger.LogDebug(ex, "Initial editor warm-up failed");
        }
    }

    private async Task RefreshSafeAsync(IUnityConnection unity) {
        try {
            await RefreshAsync(unity, CancellationToken.None);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Dynamic tool refresh failed");
        }
    }

    /// <summary>Fetch the editor's advertised tools and reconcile the server's tool collection.</summary>
    public async Task RefreshAsync(IUnityConnection unity, CancellationToken ct) {
        var response = await unity.SendAsync("unity.meta.listTools", null, ct);
        if (!response.Success || response.Result is not { } result) {
            // Older Unity-side plugin without discovery — nothing to serve dynamically.
            logger.LogDebug("Editor at {Uri} does not support unity.meta.listTools", unity.CurrentUri);
            return;
        }

        if (!result.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != System.Text.Json.JsonValueKind.Array) {
            return;
        }

        var discovered = new List<UnityProxyTool>();
        foreach (var element in toolsElement.EnumerateArray()) {
            var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
            var description = element.TryGetProperty("description", out var d) ? d.GetString() : null;
            var command = element.TryGetProperty("command", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command) || !element.TryGetProperty("inputSchema", out var schema)) {
                logger.LogWarning("Skipping malformed tool descriptor from {Uri}: {Json}", unity.CurrentUri, element.GetRawText());
                continue;
            }

            discovered.Add(new UnityProxyTool(pool, name, description ?? string.Empty, command, schema));
        }

        var collection = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
        if (collection is null) {
            logger.LogWarning("Server has no tool collection; dynamic tools cannot be registered");
            return;
        }

        await m_RefreshLock.WaitAsync(ct);
        try {
            var incomingByName = discovered.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

            // Drop dynamic tools the editor no longer advertises.
            foreach (var (name, existing) in m_DynamicTools.ToList()) {
                if (!incomingByName.ContainsKey(name)) {
                    collection.Remove(existing);
                    m_DynamicTools.Remove(name);
                    logger.LogInformation("Removed dynamic tool '{Name}'", name);
                }
            }

            foreach (var (name, incoming) in incomingByName) {
                if (m_DynamicTools.TryGetValue(name, out var existing)) {
                    if (existing.Fingerprint == incoming.Fingerprint) {
                        continue; // unchanged — keep the registered instance
                    }

                    collection.Remove(existing);
                    m_DynamicTools.Remove(name);
                }

                if (collection.TryAdd(incoming)) {
                    m_DynamicTools[name] = incoming;
                    logger.LogInformation("Registered dynamic tool '{Name}' -> {Command}", name, incoming.Fingerprint.Split('\n')[2]);
                } else {
                    // A native (attributed) tool owns this name — it shadows the dynamic one.
                    logger.LogDebug("Dynamic tool '{Name}' shadowed by a native tool", name);
                }
            }
        } finally {
            m_RefreshLock.Release();
        }
    }
}
