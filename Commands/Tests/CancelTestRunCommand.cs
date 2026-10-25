using System;
using UnityEditor.TestTools.TestRunner.Api;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Cancel an active Unity test run.
/// </summary>
public sealed class CancelTestRunCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.editor.cancelTestRun";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "cancel_test_run",
        "Cancel an active Unity test run. If runId is omitted, cancels the currently running run.",
        """{"type":"object","properties":{"runId":{"description":"Optional run identifier returned by run_tests","type":["string","null"],"default":null}}}"""
    );

    public McpResponse Execute(McpRequest request) {
        var runId = request.GetParam<string>("runId");
        if (string.IsNullOrWhiteSpace(runId)) {
            if (!McpTestRunStore.TryGetRunningRun(out runId)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "No active test run found and runId was not provided.");
            }
        }

        if (!McpTestRunStore.TryGetRunForCancellation(runId, out var run)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"No cancellable test run found for runId '{runId}'.");
        }

        try {
            if (!TestRunnerApi.CancelTestRun(run.SessionId)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"Test run {runId} could not be cancelled: it has already finished or is already being cancelled.");
            }

            McpTestRunStore.MarkCancelRequested(runId);
            return McpResponse.Ok(request.Id, $"Requested cancellation of test run {runId}.");
        } catch (Exception ex) {
            return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed,
                $"Failed to cancel test run: {ex.Message}",
                new { exception = ex.GetType().FullName, stackTrace = ex.StackTrace }
            );
        }
    }
}
