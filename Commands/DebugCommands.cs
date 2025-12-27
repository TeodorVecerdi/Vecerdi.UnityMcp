using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Captured log entry from Unity console.
/// </summary>
public sealed class LogEntry {
    public string Level { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Captures and stores Unity console logs.
/// </summary>
public sealed class LogBuffer {
    private readonly object m_Lock = new();
    private readonly LinkedList<LogEntry> m_Logs = new();
    private int m_MaxSize;

    public int MaxSize {
        get => m_MaxSize;
        set {
            m_MaxSize = value;
            TrimToSize();
        }
    }

    public LogBuffer(int maxSize = 1000) {
        m_MaxSize = maxSize;
        Application.logMessageReceived += OnLogMessage;
    }

    public void Dispose() {
        Application.logMessageReceived -= OnLogMessage;
    }

    private void OnLogMessage(string message, string stackTrace, LogType type) {
        var entry = new LogEntry {
            Level = type switch {
                LogType.Error => "error",
                LogType.Exception => "error",
                LogType.Assert => "error",
                LogType.Warning => "warning",
                _ => "info",
            },
            Message = message,
            StackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace,
            Timestamp = DateTime.UtcNow,
        };

        lock (m_Lock) {
            m_Logs.AddLast(entry);
            TrimToSize();
        }
    }

    private void TrimToSize() {
        while (m_Logs.Count > m_MaxSize) {
            m_Logs.RemoveFirst();
        }
    }

    public void Clear() {
        lock (m_Lock) {
            m_Logs.Clear();
        }
    }

    public List<LogEntry> GetLogs(int count = 100, string? minLevel = null, string? filter = null) {
        lock (m_Lock) {
            IEnumerable<LogEntry> query = m_Logs;

            // Filter by minimum level
            if (!string.IsNullOrEmpty(minLevel)) {
                var minPriority = GetLevelPriority(minLevel);
                query = query.Where(e => GetLevelPriority(e.Level) >= minPriority);
            }

            // Filter by text content
            if (!string.IsNullOrEmpty(filter)) {
                query = query.Where(e =>
                    e.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (e.StackTrace?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }

            // Take most recent entries
            return query.TakeLast(count).ToList();
        }
    }

    private static int GetLevelPriority(string level) => level.ToLowerInvariant() switch {
        "error" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0,
    };
}

/// <summary>
/// Command: unity.debug.getLogs - Get recent console log entries.
/// </summary>
public sealed class GetLogsCommand(LogBuffer logBuffer) : IMcpCommandHandler {
    public string Command => "unity.debug.getLogs";

    public McpResponse Execute(McpRequest request) {
        var count = request.GetParam("count", 100);
        var minLevel = request.GetParam<string>("minLevel");
        var filter = request.GetParam<string>("filter");

        var logs = logBuffer.GetLogs(count, minLevel, filter);

        return McpResponse.Ok(request.Id, new { logs, total = logs.Count });
    }
}

/// <summary>
/// Command: unity.debug.clearLogs - Clear the log buffer.
/// </summary>
public sealed class ClearLogsCommand(LogBuffer logBuffer) : IMcpCommandHandler {
    public string Command => "unity.debug.clearLogs";

    public McpResponse Execute(McpRequest request) {
        logBuffer.Clear();
        return McpResponse.Ok(request.Id, new { cleared = true });
    }
}
