using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.getTestRunStatus - Get status and results of a test run.
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

        if (!McpTestRunStore.TryGetSnapshot(runId!, out var snapshot)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"No test run found for runId '{runId}'.");
        }

        return McpResponse.Ok(request.Id, snapshot);
    }
}
