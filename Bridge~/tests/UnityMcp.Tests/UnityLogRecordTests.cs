using System.Text.Json;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class UnityLogRecordTests {
    private static JsonElement Payload(string json) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ReadAll_ParsesLevelMessageStackAndTimestamp() {
        var result = Payload("""
        {
          "logs": [
            { "level": "error", "message": "boom", "stackTrace": "at X()", "timestamp": "2026-07-11T10:00:00.000+00:00" },
            { "level": "info", "message": "hi", "stackTrace": null, "timestamp": "2026-07-11T10:00:01.000+00:00" }
          ]
        }
        """);

        var records = UnityLogRecord.ReadAll(result);

        Assert.Equal(2, records.Count);
        Assert.Equal("error", records[0].Level);
        Assert.Equal("boom", records[0].Message);
        Assert.Equal("at X()", records[0].StackTrace);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero), records[0].Timestamp);
        Assert.Null(records[1].StackTrace);
    }

    [Fact]
    public void ReadAll_NoLogsArray_ReturnsEmpty() {
        Assert.Empty(UnityLogRecord.ReadAll(Payload("""{ "total": 0 }""")));
    }

    [Fact]
    public void Since_KeepsOnlyEntriesStrictlyAfterMarker() {
        var marker = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
        var records = new[] {
            new UnityLogRecord { Message = "stale", Timestamp = marker.AddSeconds(-1) },
            new UnityLogRecord { Message = "at-marker", Timestamp = marker },
            new UnityLogRecord { Message = "fresh", Timestamp = marker.AddSeconds(1) },
        };

        var fresh = UnityLogRecord.Since(records, marker);

        Assert.Single(fresh);
        Assert.Equal("fresh", fresh[0].Message);
    }

    [Fact]
    public void Since_DropsEntriesWithoutTimestamp() {
        var marker = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
        var records = new[] {
            new UnityLogRecord { Message = "no-timestamp", Timestamp = null },
        };

        Assert.Empty(UnityLogRecord.Since(records, marker));
    }
}
