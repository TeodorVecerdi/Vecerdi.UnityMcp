using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityMcp;

/// <summary>
/// Represents a registered Unity Editor instance from the discovery file.
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

    /// <summary>
    /// Check if the process is still running.
    /// </summary>
    public bool IsAlive {
        get {
            try {
                Process.GetProcessById(ProcessId);
                return true;
            } catch (ArgumentException) {
                return false;
            }
        }
    }
}

/// <summary>
/// Reads the Unity MCP discovery file to find available editor instances.
/// </summary>
public static class EditorDiscovery {
    private const int FallbackPort = 9100;

    private static readonly string s_DiscoveryFilePath;
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
    };

    static EditorDiscovery() {
        var tempPath = Path.GetTempPath();
        var mcpDir = Path.Combine(tempPath, "unity-mcp");
        s_DiscoveryFilePath = Path.Combine(mcpDir, "instances.json");
    }

    /// <summary>
    /// Gets all available Unity Editor instances.
    /// </summary>
    public static List<EditorInstance> GetAvailableEditors() {
        try {
            if (!File.Exists(s_DiscoveryFilePath)) {
                return [];
            }

            var json = File.ReadAllText(s_DiscoveryFilePath);
            var instances = JsonSerializer.Deserialize<List<EditorInstance>>(json, s_JsonOptions) ?? [];

            // Filter to only alive instances
            return instances.Where(i => i.IsAlive).ToList();
        } catch {
            return [];
        }
    }

    /// <summary>
    /// Gets the URI for a specific editor instance.
    /// </summary>
    public static string GetEditorUri(EditorInstance instance) {
        return $"ws://localhost:{instance.Port}/";
    }

    /// <summary>
    /// Gets the URI for the first available editor, or fallback to default port.
    /// </summary>
    public static string GetDefaultEditorUri() {
        var editors = GetAvailableEditors();
        if (editors.Count > 0) {
            return GetEditorUri(editors[0]);
        }

        // Fallback to default port
        return $"ws://localhost:{FallbackPort}/";
    }

    /// <summary>
    /// Finds an editor by project name (case-insensitive partial match).
    /// </summary>
    public static EditorInstance? FindEditorByProject(string projectName) {
        var editors = GetAvailableEditors();
        return editors.FirstOrDefault(e =>
            e.ProjectName.Contains(projectName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds an editor by port.
    /// </summary>
    public static EditorInstance? FindEditorByPort(int port) {
        var editors = GetAvailableEditors();
        return editors.FirstOrDefault(e => e.Port == port);
    }
}
