using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Get recent console log entries formatted as text for the get_logs tool.
/// </summary>
public sealed class GetLogsCommand(LogBuffer logBuffer) : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.debug.getLogs";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "get_logs",
        "Get recent Unity console logs. Useful for seeing compilation errors, runtime exceptions, and debug output.",
        """{"type":"object","properties":{"count":{"description":"Maximum number of log entries to return (default: 100)","type":"integer","default":100},"minLevel":{"description":"Minimum log level to include: info, warning, or error","type":["string","null"],"default":null},"filter":{"description":"Filter logs containing this text (case-insensitive)","type":["string","null"],"default":null},"includeStackTraces":{"description":"Include stack traces for each log entry (very verbose; default: false)","type":"boolean","default":false}}}"""
    );

    public McpResponse Execute(McpRequest request) {
        var count = request.GetParam("count", 100);
        var minLevel = request.GetParam<string>("minLevel");
        var filter = request.GetParam<string>("filter");
        var includeStackTraces = request.GetParam("includeStackTraces", false);

        var logs = logBuffer.GetLogs(count, minLevel, filter);
        return McpResponse.Ok(request.Id, FormatLogs(logs, includeStackTraces));
    }

    private string FormatLogs(List<LogEntry> logs, bool includeStackTraces) {
        var note = GetNote();
        if (logs.Count == 0) {
            return note is not null
                ? $"No logs matching the criteria.\n{note}"
                : "No logs matching the criteria.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {logs.Count} log entries:");
        if (note is not null)
            sb.AppendLine(note);
        sb.AppendLine();

        foreach (var record in logs) {
            sb.AppendLine($"[{record.Timestamp.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{record.Level.ToUpperInvariant()}] {record.Message}");
            if (includeStackTraces && !string.IsNullOrEmpty(record.StackTrace)) {
                sb.AppendLine($"  Stack: {record.StackTrace}");
            }
        }

        return sb.ToString();
    }

    private string? GetNote() {
        if (logBuffer.DroppedSinceStart <= 0)
            return null;
        var dropped = logBuffer.DroppedSinceStart;
        return $"Note: log buffer wrapped - {dropped} older entr{(dropped == 1 ? "y has" : "ies have")} been dropped since the editor started (buffer holds {logBuffer.MaxSize}). Older logs are no longer retrievable.";
    }
}
