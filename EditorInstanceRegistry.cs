using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Vecerdi.UnityMcp;

/// <summary>
/// Represents a registered Unity Editor instance.
/// </summary>
public sealed class EditorInstance {
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>One of <see cref="EditorInstanceState"/>. Lets the stdio bridge tell a domain reload from a closed editor.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = EditorInstanceState.Ready;

    [JsonPropertyName("stateChangedAt")]
    public DateTime StateChangedAt { get; set; }

    /// <summary>When the most recent script compilation started (UTC). Survives the reload that follows a successful compile.</summary>
    [JsonPropertyName("compilationStartedAt")]
    public DateTime? CompilationStartedAt { get; set; }
}

/// <summary>
/// Lifecycle states an editor advertises in the discovery file. The entry itself stays put across a domain reload;
/// only quitting removes it, so a missing entry really does mean "no editor".
/// </summary>
public static class EditorInstanceState {
    public const string Ready = "ready";
    public const string Compiling = "compiling";
    public const string Reloading = "reloading";
}

/// <summary>
/// Manages the discovery file for Unity Editor instances.
/// </summary>
public static class EditorInstanceRegistry {
    private const int MinPort = 9100;
    private const int MaxPort = 9200;

    private static readonly string s_DiscoveryFilePath;
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        WriteIndented = true,
    };

    static EditorInstanceRegistry() {
        var tempPath = Path.GetTempPath();
        var mcpDir = Path.Combine(tempPath, "unity-mcp");
        s_DiscoveryFilePath = Path.Combine(mcpDir, "instances.json");
    }

    /// <summary>
    /// Gets the path to the discovery file.
    /// </summary>
    public static string DiscoveryFilePath => s_DiscoveryFilePath;

    /// <summary>
    /// Finds an available port and registers this editor instance.
    /// </summary>
    /// <returns>The allocated port, or -1 if no port is available.</returns>
    public static int RegisterInstance(ILogger? logger = null) {
        var instances = LoadInstances(logger);

        // The entry this editor wrote before its last domain reload (same project path). Re-registering replaces it,
        // but we keep its port and compile timestamp so the bridge sees the same editor come back, not a new one.
        var currentProjectPath = Application.dataPath.Replace("/Assets", "");
        var previous = instances.FirstOrDefault(i => string.Equals(i.ProjectPath, currentProjectPath, StringComparison.OrdinalIgnoreCase));

        // Clean up stale instances (processes that no longer exist, or duplicate project paths)
        instances = CleanupStaleInstances(instances, currentProjectPath, logger);

        // Find an available port, preferring the one this editor used before the reload
        var usedPorts = instances.Select(i => i.Port).ToHashSet();
        var port = -1;

        if (previous is { Port: >= MinPort and <= MaxPort } && !usedPorts.Contains(previous.Port) && IsPortAvailable(previous.Port)) {
            port = previous.Port;
        }

        for (var p = MinPort; port == -1 && p <= MaxPort; p++) {
            if (!usedPorts.Contains(p) && IsPortAvailable(p)) {
                port = p;
            }
        }

        if (port == -1) {
            logger?.LogError("No available ports in range {MinPort}-{MaxPort}", MinPort, MaxPort);
            return -1;
        }

        // Register this instance
        var instance = new EditorInstance {
            Port = port,
            ProjectPath = currentProjectPath,
            ProjectName = Path.GetFileName(currentProjectPath),
            ProcessId = Process.GetCurrentProcess().Id,
            StartTime = DateTime.UtcNow,
            State = EditorInstanceState.Ready,
            StateChangedAt = DateTime.UtcNow,
            CompilationStartedAt = previous?.CompilationStartedAt,
        };

        instances.Add(instance);
        SaveInstances(instances, logger);

        logger?.LogDebug("Registered instance on port {Port} (PID: {ProcessId})", port, instance.ProcessId);

        return port;
    }

    /// <summary>
    /// Unregisters this editor instance from the discovery file.
    /// </summary>
    public static void UnregisterInstance(int port, ILogger? logger = null) {
        var instances = LoadInstances(logger);
        var pid = Process.GetCurrentProcess().Id;

        var removed = instances.RemoveAll(i => i.Port == port && i.ProcessId == pid);

        if (removed > 0) {
            SaveInstances(instances, logger);
            logger?.LogDebug("Unregistered instance on port {Port}", port);
        }
    }

    /// <summary>
    /// Advertises a lifecycle state for this editor's entry without touching its port or identity.
    /// </summary>
    /// <param name="compilationStartedAt">When set, also records the start of the current script compilation.</param>
    /// <param name="clearCompilationStartedAt">Forget the recorded compilation start (a reload that no compile led to).</param>
    public static void UpdateState(int port, string state, DateTime? compilationStartedAt = null, bool clearCompilationStartedAt = false, ILogger? logger = null) {
        var instances = LoadInstances(logger);
        var pid = Process.GetCurrentProcess().Id;
        var instance = instances.FirstOrDefault(i => i.Port == port && i.ProcessId == pid);
        if (instance is null) {
            return;
        }

        instance.State = state;
        instance.StateChangedAt = DateTime.UtcNow;
        if (compilationStartedAt is { } startedAt) {
            instance.CompilationStartedAt = startedAt;
        } else if (clearCompilationStartedAt) {
            instance.CompilationStartedAt = null;
        }

        SaveInstances(instances, logger);
        logger?.LogDebug("Instance on port {Port} is now {State}", port, state);
    }

    /// <summary>
    /// Gets all registered instances.
    /// </summary>
    public static List<EditorInstance> GetInstances(ILogger? logger = null) {
        var instances = LoadInstances(logger);
        return CleanupStaleInstances(instances, currentProjectPath: null, logger);
    }

    private static List<EditorInstance> LoadInstances(ILogger? logger) {
        try {
            if (!File.Exists(s_DiscoveryFilePath)) {
                return [];
            }

            var json = File.ReadAllText(s_DiscoveryFilePath);
            return JsonSerializer.Deserialize<List<EditorInstance>>(json, s_JsonOptions) ?? [];
        } catch (Exception ex) {
            logger?.LogWarning(ex, "Failed to load discovery file");
            return [];
        }
    }

    private static void SaveInstances(List<EditorInstance> instances, ILogger? logger) {
        try {
            var directory = Path.GetDirectoryName(s_DiscoveryFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(instances, s_JsonOptions);
            File.WriteAllText(s_DiscoveryFilePath, json);
        } catch (Exception ex) {
            logger?.LogWarning(ex, "Failed to save discovery file");
        }
    }

    private static List<EditorInstance> CleanupStaleInstances(List<EditorInstance> instances, string? currentProjectPath, ILogger? logger) {
        var validInstances = new List<EditorInstance>();

        foreach (var instance in instances) {
            // Remove entries with the same project path as the current instance
            // (Unity doesn't allow opening the same project twice, so this must be stale)
            if (currentProjectPath != null && 
                string.Equals(instance.ProjectPath, currentProjectPath, StringComparison.OrdinalIgnoreCase)) {
                logger?.LogDebug("Removing stale instance on port {Port} (duplicate project path: {ProjectPath})",
                    instance.Port, instance.ProjectPath);
                continue;
            }

            // Check if the process still exists
            if (!IsProcessRunning(instance.ProcessId)) {
                logger?.LogDebug("Removing stale instance on port {Port} (PID: {ProcessId} no longer exists)",
                    instance.Port, instance.ProcessId);
                continue;
            }

            validInstances.Add(instance);
        }

        // Save if we removed any stale instances
        if (validInstances.Count != instances.Count) {
            SaveInstances(validInstances, logger);
        }

        return validInstances;
    }

    private static bool IsProcessRunning(int processId) {
        try {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        } catch (ArgumentException) {
            // Process doesn't exist
            return false;
        } catch (InvalidOperationException) {
            // Process has exited
            return false;
        }
    }

    private static bool IsPortAvailable(int port) {
        try {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        } catch {
            return false;
        }
    }
}
