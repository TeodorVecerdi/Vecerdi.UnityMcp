using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Get status and results of a test run.
/// </summary>
public sealed class GetTestRunStatusCommand : IMcpCommandHandler {
    public string Command => "unity.editor.getTestRunStatus";

    public McpResponse Execute(McpRequest request) {
        var runId = request.GetParam<string>("runId");
        if (string.IsNullOrWhiteSpace(runId)) {
            if (!McpTestRunStore.TryGetLatestRunId(out runId)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams,
                    "runId is required when no previous test run exists.");
            }
        }

        if (!McpTestRunStore.TryGetRunSummary(runId, out var status, out var summary)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"No test run found for runId '{runId}'.");
        }

        return McpResponse.Ok(request.Id, new {
            runId,
            status,
            summary,
        });
    }
}
