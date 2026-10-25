using System;
using System.Collections.Generic;
using Vecerdi.UnityMcp.Protocol;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.managed.getInvocationResult - Poll a pending invocation started by
/// unity.bridge.invokeManagedMethod (todo #387).
/// </summary>
public sealed class GetInvocationResultCommand : IMcpCommandHandler, IMcpToolProvider {
    public string Command => "unity.managed.getInvocationResult";

    public McpToolDescriptor ToolDescriptor { get; } = new(
        "get_invocation_result",
        "Poll the outcome of a backgrounded invoke_managed_method call. When invoke_managed_method returns {pending: true, invocationId}, the invoked Task kept running without blocking the editor; call this with that invocationId until status is 'completed' (includes returnValue) or the call reports the failure. Results are handed out once - the pending entry is removed on a completed/faulted poll, and unclaimed entries expire after ~1 hour or on domain reload.",
        """{"type":"object","properties":{"invocationId":{"type":"string","description":"The invocationId returned by a pending invoke_managed_method call"}},"required":["invocationId"]}""");

    public McpResponse Execute(McpRequest request) {
        var invocationId = request.GetParam<string>("invocationId");
        if (string.IsNullOrWhiteSpace(invocationId)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, "invocationId is required");
        }

        if (!PendingInvocationRegistry.TryGet(invocationId, out var task, out var method, out var elapsed)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, $"Unknown or expired invocationId '{invocationId}' (results are handed out once; entries expire after an hour or on domain reload).");
        }

        if (!task.IsCompleted) {
            return McpResponse.Ok(request.Id, new Dictionary<string, object?> {
                ["status"] = "running",
                ["method"] = method,
                ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
            });
        }

        PendingInvocationRegistry.Remove(invocationId);

        if (task.IsCanceled) {
            return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed, $"Invocation '{method}' was cancelled.");
        }

        if (task.IsFaulted) {
            var inner = task.Exception?.GetBaseException() ?? new Exception("Unknown failure");
            return McpResponse.Fail(
                request.Id,
                McpErrorCodes.ExecutionFailed,
                $"Invocation '{method}' failed: {inner.Message}",
                new {
                    exception = inner.GetType().FullName,
                    stackTrace = inner.StackTrace,
                }
            );
        }

        return McpResponse.Ok(request.Id, new Dictionary<string, object?> {
            ["status"] = "completed",
            ["method"] = method,
            ["returnValue"] = BridgeInvokeManagedMethodCommand.SerializeResult(BridgeInvokeManagedMethodCommand.ExtractTaskResult(task)),
        });
    }
}