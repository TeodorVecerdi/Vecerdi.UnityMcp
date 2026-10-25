using System;
using System.Linq;
using System.Reflection;
using UnityEditor.TestTools.TestRunner.Api;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.cancelTestRun - Cancel an active test run.
/// </summary>
public sealed class CancelTestRunCommand : IMcpCommandHandler {
    public string Command => "unity.editor.cancelTestRun";

    public McpResponse Execute(McpRequest request) {
        var runId = request.GetParam<string>("runId");
        if (string.IsNullOrWhiteSpace(runId)) {
            if (!McpTestRunStore.TryGetRunningRun(out runId)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams,
                    "No active test run found and runId was not provided.");
            }
        }

        if (!McpTestRunStore.TryGetRunForCancellation(runId!, out var run)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"No cancellable test run found for runId '{runId}'.");
        }

        try {
            McpTestRunStore.MarkCancelRequested(runId!);

            if (!TryInvokeCancel(run.SessionId, run.Api!)) {
                return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                    "Unable to cancel test run. TestRunnerApi.CancelTestRun was not found for this Unity version.");
            }

            return McpResponse.Ok(request.Id, new {
                runId,
                cancelled = true,
            });
        } catch (Exception ex) {
            return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed,
                $"Failed to cancel test run: {ex.Message}",
                new { exception = ex.GetType().FullName, stackTrace = ex.StackTrace });
        }
    }

    private static bool TryInvokeCancel(string? sessionId, TestRunnerApi api) {
        var type = typeof(TestRunnerApi);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var candidates = type.GetMethods(flags)
            .Where(m => string.Equals(m.Name, "CancelTestRun", StringComparison.Ordinal))
            .ToList();

        foreach (var method in candidates) {
            var parameters = method.GetParameters();

            try {
                object? target = method.IsStatic ? null : api;

                if (parameters.Length == 0) {
                    method.Invoke(target, []);
                    return true;
                }

                if (parameters.Length == 1) {
                    var parameterType = parameters[0].ParameterType;

                    if (parameterType == typeof(string)) {
                        method.Invoke(target, [sessionId ?? string.Empty]);
                        return true;
                    }

                    if (parameterType == typeof(Guid) && Guid.TryParse(sessionId, out var guid)) {
                        method.Invoke(target, [guid]);
                        return true;
                    }
                }
            } catch {
                // Try next signature.
            }
        }

        return false;
    }
}
