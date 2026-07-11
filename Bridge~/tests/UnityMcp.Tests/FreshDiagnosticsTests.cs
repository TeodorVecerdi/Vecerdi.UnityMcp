using ModelContextProtocol.Protocol;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

public sealed class FreshDiagnosticsTests {
    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task BuildFreshDiagnostics_NoErrorsAfterMarker_ReportsSuccess() {
        var marker = DateTimeOffset.UtcNow;
        var connection = new ScriptedUnityConnection((command, _) =>
            command == "unity.debug.getLogs"
                ? ScriptedUnityConnection.LogsResponse(("error", "old runtime error", marker.AddSeconds(-5)))
                : new UnityResponse { Success = true });

        var result = await UnityTools.BuildFreshDiagnosticsResultAsync(connection, marker, settled: true, CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Contains("no errors", TextOf(result));
    }

    [Fact]
    public async Task BuildFreshDiagnostics_ExcludesStaleErrors_ButReportsFreshOnes() {
        var marker = DateTimeOffset.UtcNow;
        var connection = new ScriptedUnityConnection((command, _) =>
            command == "unity.debug.getLogs"
                ? ScriptedUnityConnection.LogsResponse(
                    ("error", "NullReferenceException from a previous play session", marker.AddSeconds(-10)),
                    ("error", "Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist", marker.AddSeconds(2)))
                : new UnityResponse { Success = true });

        var result = await UnityTools.BuildFreshDiagnosticsResultAsync(connection, marker, settled: true, CancellationToken.None);

        var text = TextOf(result);
        Assert.True(result.IsError);
        Assert.Contains("Compilation FAILED with 1 new error", text);
        Assert.Contains("Assets/Scripts/Foo.cs(12,20): error CS0103", text);
        Assert.DoesNotContain("NullReferenceException", text);
    }

    [Fact]
    public async Task BuildFreshDiagnostics_UnparseableFreshError_StillSurfacedRaw() {
        var marker = DateTimeOffset.UtcNow;
        var connection = new ScriptedUnityConnection((command, _) =>
            command == "unity.debug.getLogs"
                ? ScriptedUnityConnection.LogsResponse(
                    ("error", "Internal compiler error: something exploded", marker.AddSeconds(1)))
                : new UnityResponse { Success = true });

        var result = await UnityTools.BuildFreshDiagnosticsResultAsync(connection, marker, settled: true, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Internal compiler error", TextOf(result));
    }
}
