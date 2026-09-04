using System.Text.Json;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class TestRunSummaryTests {
    private static JsonElement Snapshot(string json) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void FormatTestRunSummary_PassedRun_LabelsRanVsDiscoveredAndEchoesFilter() {
        var summary = UnityTools.FormatTestRunSummary(Snapshot("""
        {
          "runId": "abc",
          "status": "passed",
          "totals": { "discovered": 1803, "executed": 5, "passed": 5, "failed": 0, "skipped": 0, "inconclusive": 0, "other": 0 },
          "filter": { "testMode": "EditMode", "assemblyNames": ["MyGame.Tests"], "testNames": [], "categoryNames": [], "groupNames": [], "targetPlatform": null }
        }
        """));

        Assert.Contains("5 passed of 5 ran", summary);
        Assert.Contains("discovered (entire test tree", summary);
        Assert.Contains("1803", summary);
        Assert.Contains("resolved filter:", summary);
        Assert.Contains("assemblyNames=[MyGame.Tests]", summary);
        Assert.DoesNotContain("WARNING", summary);
    }

    [Fact]
    public void FormatTestRunSummary_ZeroMatched_WarnsAboutFilter() {
        var summary = UnityTools.FormatTestRunSummary(Snapshot("""
        {
          "runId": "abc",
          "status": "passed",
          "totals": { "discovered": 1803, "executed": 0, "passed": 0, "failed": 0, "skipped": 0, "inconclusive": 0, "other": 0 },
          "filter": { "testMode": "EditMode", "testNames": ["MyGame.Tests.DoesNotExist"] }
        }
        """));

        Assert.Contains("WARNING: the filter matched 0 tests", summary);
        Assert.Contains("testNames=[MyGame.Tests.DoesNotExist]", summary);
    }

    [Fact]
    public void FormatTestRunSummary_FailedRun_IncludesFailingIdsAndMessagesInline() {
        var summary = UnityTools.FormatTestRunSummary(Snapshot("""
        {
          "runId": "abc",
          "status": "failed",
          "totals": { "discovered": 1803, "executed": 3, "passed": 2, "failed": 1, "skipped": 0, "inconclusive": 0, "other": 0 },
          "filter": { "testMode": "EditMode" },
          "failures": [
            { "name": "MyGame.Tests.FooTests.Bar", "message": "Expected 1 but was 2", "stackTrace": "at Foo()" }
          ]
        }
        """));

        Assert.Contains("1 failed, 2 passed of 3 ran", summary);
        Assert.Contains("MyGame.Tests.FooTests.Bar", summary);
        Assert.Contains("Expected 1 but was 2", summary);
    }

    [Fact]
    public void DescribeResolvedFilter_NoNameFilters_SaysRunsEverything() {
        var filter = UnityTools.DescribeResolvedFilter(Snapshot("""{ "filter": { "testMode": "EditMode" } }"""));
        Assert.Contains("runs everything", filter);
    }

    [Fact]
    public void DescribeResolvedFilter_MissingFilter_ReportsNone() {
        Assert.Equal("(none reported)", UnityTools.DescribeResolvedFilter(Snapshot("""{ "status": "passed" }""")));
    }
}
