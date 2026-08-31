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
    /// Lifecycle state advertised by the editor: <c>ready</c>, <c>compiling</c> (socket still up) or
    /// <c>reloading</c> (socket down for a domain reload; the entry stays so the bridge can wait instead of
    /// declaring the editor gone). Editors running an older plugin never write it and read as ready.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = EditorInstanceState.Ready;

    [JsonPropertyName("stateChangedAt")]
    public DateTimeOffset? StateChangedAt { get; set; }

    /// <summary>
    /// When the script compilation the editor is currently in (or the reload it led to) started. Null once the
    /// editor is back to ready, and null on a reload that no compile caused.
    /// </summary>
    [JsonPropertyName("compilationStartedAt")]
    public DateTimeOffset? CompilationStartedAt { get; set; }

    public bool IsReloading => string.Equals(State, EditorInstanceState.Reloading, StringComparison.OrdinalIgnoreCase);
    public bool IsCompiling => string.Equals(State, EditorInstanceState.Compiling, StringComparison.OrdinalIgnoreCase);

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

/// <summary>Values of <see cref="EditorInstance.State"/>; mirrors the editor plugin's constants.</summary>
public static class EditorInstanceState {
    public const string Ready = "ready";
    public const string Compiling = "compiling";
    public const string Reloading = "reloading";
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
        return GetEditorUri(instance.Port);
    }

    /// <summary>
    /// Gets the URI for an editor on a specific port.
    /// </summary>
    public static string GetEditorUri(int port) {
        return $"ws://localhost:{port}/";
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

    /// <summary>The port of a <c>ws://host:port/</c> editor URI, or null when it does not parse.</summary>
    public static int? TryGetPort(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Port > 0 ? parsed.Port : null;

    /// <summary>
    /// A short agent-facing description of why the editor on <paramref name="port"/> is not answering right now,
    /// or null when the registry does not show an interruption.
    /// </summary>
    public static string? DescribeInterruption(int port) {
        var editor = FindEditorByPort(port);
        if (editor is null) {
            return $"the editor on port {port} is no longer registered (closed?)";
        }

        if (!editor.IsReloading && !editor.IsCompiling) {
            return null;
        }

        var what = editor.IsReloading ? "reloading the script domain" : "compiling scripts";
        var since = editor.StateChangedAt is { } at ? $" for {(DateTimeOffset.UtcNow - at).TotalSeconds:F0}s" : "";
        return $"Unity Editor '{editor.ProjectName}' (port {port}) is {what}{since}";
    }
}
