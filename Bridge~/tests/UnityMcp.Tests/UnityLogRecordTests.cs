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

    [Fact]
    public void TimestampLabel_FormatsUtcOrDashes() {
        var withTs = new UnityLogRecord { Timestamp = new DateTimeOffset(2026, 7, 11, 13, 5, 9, 123, TimeSpan.Zero) };
        Assert.Equal("13:05:09.123", withTs.TimestampLabel());
        Assert.Equal("--", new UnityLogRecord { Timestamp = null }.TimestampLabel());
    }

    [Fact]
    public void LogBufferInfo_Wrapped_ProducesNote() {
        var info = LogBufferInfo.FromResult(Payload("""{ "droppedSinceStart": 42, "capacity": 1000 }"""));
        Assert.True(info.Wrapped);
        Assert.Contains("42 older entries", info.Note());
        Assert.Contains("buffer holds 1000", info.Note());
    }

    [Fact]
    public void LogBufferInfo_NoDrops_HasNoNote() {
        var info = LogBufferInfo.FromResult(Payload("""{ "droppedSinceStart": 0, "capacity": 1000 }"""));
        Assert.False(info.Wrapped);
        Assert.Null(info.Note());
    }

    [Fact]
    public void LogBufferInfo_FieldAbsent_IsUnknownAndSilent() {
        var info = LogBufferInfo.FromResult(Payload("""{ "total": 3 }"""));
        Assert.Null(info.DroppedSinceStart);
        Assert.False(info.Wrapped);
        Assert.Null(info.Note());
    }
}
