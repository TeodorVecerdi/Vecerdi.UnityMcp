using System.Text.Json;
using UnityMcp.Mcp;

namespace UnityMcp;

/// <summary>
/// Defines an MCP tool that maps to a Unity command.
/// </summary>
public sealed class UnityTool {
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string UnityCommand { get; init; }
    public InputSchema InputSchema { get; init; } = new();

    /// <summary>
    /// Transform MCP arguments to Unity parameters (optional).
    /// </summary>
    public Func<Dictionary<string, object>?, object?>? TransformParams { get; init; }

    /// <summary>
    /// Format Unity response for MCP (optional).
    /// </summary>
    public Func<JsonElement?, string>? FormatResponse { get; init; }

    public ToolDefinition ToDefinition() => new() {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
    };
}

/// <summary>
/// Registry of all Unity tools exposed via MCP.
/// </summary>
public static class UnityTools {
    private static readonly JsonSerializerOptions s_JsonOptions = new() {
        WriteIndented = true,
    };

    public static readonly List<UnityTool> All = [
        // Debug commands
        new UnityTool {
            Name = "get_logs",
            Description = "Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.",
            UnityCommand = "unity.debug.getLogs",
            InputSchema = new InputSchema {
                Properties = new Dictionary<string, PropertySchema> {
                    ["count"] = new() {
                        Type = "integer",
                        Description = "Maximum number of log entries to return (default: 100)",
                        Default = 100,
                    },
                    ["minLevel"] = new() {
                        Type = "string",
                        Description = "Minimum log level to include",
                        Enum = ["info", "warning", "error"],
                    },
                    ["filter"] = new() {
                        Type = "string",
                        Description = "Filter logs containing this text (case-insensitive)",
                    },
                },
            },
            FormatResponse = result => {
                if (result is null) return "No logs available.";

                if (result.Value.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array) {
                    var count = logs.GetArrayLength();
                    if (count == 0) return "No logs matching the criteria.";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Found {count} log entries:");
                    sb.AppendLine();

                    foreach (var log in logs.EnumerateArray()) {
                        var level = log.GetProperty("level").GetString()?.ToUpper() ?? "INFO";
                        var message = log.GetProperty("message").GetString() ?? "";
                        var timestamp = log.TryGetProperty("timestamp", out var ts)
                            ? ts.GetString() ?? ""
                            : "";

                        sb.AppendLine($"[{level}] {message}");

                        if (log.TryGetProperty("stackTrace", out var stackTrace) &&
                            stackTrace.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrEmpty(stackTrace.GetString())) {
                            sb.AppendLine($"  Stack: {stackTrace.GetString()}");
                        }
                    }

                    return sb.ToString();
                }

                return JsonSerializer.Serialize(result, s_JsonOptions);
            },
        },

        new UnityTool {
            Name = "clear_logs",
            Description = "Clear the Unity console log buffer.",
            UnityCommand = "unity.debug.clearLogs",
        },

        // Editor commands
        new UnityTool {
            Name = "recompile",
            Description = "Force Unity to recompile all scripts. Use this after making code changes to verify they compile. This is a blocking call that waits for compilation to complete and returns any errors.",
            UnityCommand = "unity.editor.recompile",
            // Note: FormatResponse not used - special handling in Program.cs
        },

        new UnityTool {
            Name = "get_compilation_status",
            Description = "Check if Unity is currently compiling scripts.",
            UnityCommand = "unity.editor.getCompilationStatus",
            FormatResponse = result => {
                if (result is null) return "Unable to get compilation status.";

                var isCompiling = result.Value.TryGetProperty("isCompiling", out var c) && c.GetBoolean();
                var isUpdating = result.Value.TryGetProperty("isUpdating", out var u) && u.GetBoolean();

                if (isCompiling) return "Unity is currently compiling scripts...";
                if (isUpdating) return "Unity is updating (importing assets, etc.)...";
                return "Unity is idle (not compiling).";
            },
        },

        new UnityTool {
            Name = "get_play_mode_state",
            Description = "Check if Unity Editor is in play mode, paused, or stopped.",
            UnityCommand = "unity.editor.isPlaying",
            FormatResponse = result => {
                if (result is null) return "Unable to get play mode state.";

                var isPlaying = result.Value.TryGetProperty("isPlaying", out var p) && p.GetBoolean();
                var isPaused = result.Value.TryGetProperty("isPaused", out var pa) && pa.GetBoolean();

                if (!isPlaying) return "Unity is in Edit mode (not playing).";
                if (isPaused) return "Unity is in Play mode (PAUSED).";
                return "Unity is in Play mode (running).";
            },
        },

        new UnityTool {
            Name = "enter_play_mode",
            Description = "Start Play mode in the Unity Editor to test the game.",
            UnityCommand = "unity.editor.enterPlayMode",
            FormatResponse = result => {
                if (result is null) return "Failed to enter play mode.";

                var entered = result.Value.TryGetProperty("entered", out var e) && e.GetBoolean();
                if (entered) return "Entered Play mode.";

                var reason = result.Value.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
                return $"Could not enter Play mode: {reason}";
            },
        },

        new UnityTool {
            Name = "exit_play_mode",
            Description = "Stop Play mode and return to Edit mode.",
            UnityCommand = "unity.editor.exitPlayMode",
            FormatResponse = result => {
                if (result is null) return "Failed to exit play mode.";

                var exited = result.Value.TryGetProperty("exited", out var e) && e.GetBoolean();
                if (exited) return "Exited Play mode.";

                var reason = result.Value.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown reason";
                return $"Could not exit Play mode: {reason}";
            },
        },

        new UnityTool {
            Name = "pause_play_mode",
            Description = "Pause the game while in Play mode.",
            UnityCommand = "unity.editor.pausePlayMode",
        },

        new UnityTool {
            Name = "resume_play_mode",
            Description = "Resume the game after pausing in Play mode.",
            UnityCommand = "unity.editor.resumePlayMode",
        },

        new UnityTool {
            Name = "get_open_scenes",
            Description = "Get a list of currently open scenes in the Unity Editor.",
            UnityCommand = "unity.editor.getOpenScenes",
            FormatResponse = result => {
                if (result is null) return "Unable to get open scenes.";

                if (result.Value.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array) {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Open scenes:");

                    foreach (var scene in scenes.EnumerateArray()) {
                        var name = scene.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
                        var path = scene.TryGetProperty("path", out var p) ? p.GetString() : "";
                        var isDirty = scene.TryGetProperty("isDirty", out var d) && d.GetBoolean();

                        sb.AppendLine($"  - {name}{(isDirty ? " (unsaved)" : "")}");
                        if (!string.IsNullOrEmpty(path)) {
                            sb.AppendLine($"    Path: {path}");
                        }
                    }

                    return sb.ToString();
                }

                return "No scenes open.";
            },
        },

        new UnityTool {
            Name = "save_all",
            Description = "Save all open scenes and modified assets.",
            UnityCommand = "unity.editor.saveAll",
            FormatResponse = _ => "All scenes and assets saved.",
        },

        new UnityTool {
            Name = "refresh_assets",
            Description = "Refresh the Unity Asset Database to detect external file changes.",
            UnityCommand = "unity.editor.refreshAssets",
            FormatResponse = _ => "Asset database refreshed.",
        },
    ];

    public static UnityTool? GetByName(string name) => All.Find(t => t.Name == name);
}
