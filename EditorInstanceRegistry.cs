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

        // Clean up stale instances (processes that no longer exist, or duplicate project paths)
        var currentProjectPath = Application.dataPath.Replace("/Assets", "");
        instances = CleanupStaleInstances(instances, currentProjectPath, logger);

        // Find an available port
        var usedPorts = instances.Select(i => i.Port).ToHashSet();
        var port = -1;

        for (var p = MinPort; p <= MaxPort; p++) {
            if (!usedPorts.Contains(p) && IsPortAvailable(p)) {
                port = p;
                break;
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
