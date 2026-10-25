using System;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Captured log entry from Unity console.
/// </summary>
public sealed class LogEntry {
    public string Level { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
