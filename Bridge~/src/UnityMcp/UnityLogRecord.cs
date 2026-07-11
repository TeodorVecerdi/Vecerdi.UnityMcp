using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UnityMcp;

/// <summary>
/// A console log entry as returned by the Unity plugin's <c>unity.debug.getLogs</c> command,
/// including the capture timestamp the plugin stamps on every entry. Kept as a plain record so the
/// fresh-diagnostics and formatting logic can be unit-tested without a live editor.
/// </summary>
public sealed record UnityLogRecord {
    public string Level { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }

    /// <summary>Capture time reported by the plugin, or <c>null</c> when the payload omitted it.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Read the <c>logs</c> array from a <c>unity.debug.getLogs</c> result payload into records.
    /// Returns an empty list when the payload has no array.
    /// </summary>
    public static IReadOnlyList<UnityLogRecord> ReadAll(JsonElement result) {
        if (!result.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array) {
            return [];
        }

        var records = new List<UnityLogRecord>(logs.GetArrayLength());
        foreach (var log in logs.EnumerateArray()) {
            records.Add(FromJson(log));
        }

        return records;
    }

    public static UnityLogRecord FromJson(JsonElement log) {
        var level = log.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() ?? "info"
            : "info";
        var message = log.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? string.Empty
            : string.Empty;
        var stack = log.TryGetProperty("stackTrace", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        DateTimeOffset? timestamp = null;
        if (log.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) {
            timestamp = parsed;
        }

        return new UnityLogRecord {
            Level = level,
            Message = message,
            StackTrace = string.IsNullOrEmpty(stack) ? null : stack,
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Keep only entries stamped strictly after <paramref name="marker"/>. Entries with no timestamp
    /// are dropped, since we cannot prove they are fresh — this is the core of the "never resurface
    /// pre-compile runtime errors" guarantee.
    /// </summary>
    public static IReadOnlyList<UnityLogRecord> Since(IEnumerable<UnityLogRecord> records, DateTimeOffset marker) =>
        records.Where(r => r.Timestamp is { } ts && ts > marker).ToList();

    /// <summary>Format a timestamp for display, HH:mm:ss.fff in UTC, or "--" when absent.</summary>
    public string TimestampLabel() =>
        Timestamp is { } ts ? ts.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "--";
}

/// <summary>
/// Buffer-wrap accounting reported by the plugin's log buffer (how many entries were evicted because
/// the ring buffer filled). Lets a caller distinguish "no new errors" from "older entries scrolled
/// out of the buffer". Gracefully degrades to <see cref="Unknown"/> when the plugin predates the field.
/// </summary>
public readonly record struct LogBufferInfo(int? DroppedSinceStart, int? Capacity) {
    public static LogBufferInfo Unknown => new(null, null);

    public bool Wrapped => DroppedSinceStart is > 0;

    public static LogBufferInfo FromResult(JsonElement result) {
        int? dropped = result.TryGetProperty("droppedSinceStart", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dv)
            ? dv
            : null;
        int? capacity = result.TryGetProperty("capacity", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cv)
            ? cv
            : null;
        return new LogBufferInfo(dropped, capacity);
    }

    /// <summary>A one-line human note about buffer wrap, or <c>null</c> when nothing noteworthy.</summary>
    public string? Note() {
        if (DroppedSinceStart is not { } dropped) return null;
        if (dropped <= 0) return null;
        var cap = Capacity is { } c ? $" (buffer holds {c})" : "";
        return $"Note: log buffer wrapped - {dropped} older entr{(dropped == 1 ? "y has" : "ies have")} been dropped since the editor started{cap}. Older logs are no longer retrievable.";
    }
}
