using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Get status and results of a test run.
/// </summary>
public sealed class GetTestRunStatusSummaryCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.getTestRunStatusSummary";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "get_test_run_status",
        "Get status and results for a Unity test run. If runId is omitted, returns the LATEST run - but 'latest' is per-editor (pass the matching 'port') and is undefined after a domain reload, which discards in-memory run history; prefer passing the runId that run_tests returned. Output labels matched-and-ran vs the whole-tree discovered count and echoes the resolved filter.",
        """{"type":"object","properties":{"runId":{"description":"Optional run identifier returned by run_tests","type":["string","null"],"default":null}}}"""
    );

    public McpResponse Execute(McpRequest request) {
        var runId = request.GetParam<string>("runId");
        if (string.IsNullOrWhiteSpace(runId)) {
            if (!McpTestRunStore.TryGetLatestRunId(out runId)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams,
                    "runId is required when no previous test run exists.");
            }
        }

        if (!McpTestRunStore.TryGetRunSummary(runId, out _, out var summary)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"No test run found for runId '{runId}'.");
        }

        return McpResponse.Ok(request.Id, summary);
    }
}
