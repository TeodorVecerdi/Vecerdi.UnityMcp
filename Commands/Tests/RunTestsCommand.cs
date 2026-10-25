using System;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Vecerdi.UnityMcp.Protocol;
using Object = UnityEngine.Object;

namespace Vecerdi.UnityMcp.Commands;

/// <summary>
/// Command: unity.editor.runTests - Start a Unity Test Runner execution.
/// </summary>
public sealed class RunTestsCommand : IMcpCommandHandler {
    public string Command => "unity.editor.runTests";

    public McpResponse Execute(McpRequest request) {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                "Cannot start tests while Unity is compiling or updating.");
        }

        if (McpTestRunStore.TryGetRunningRun(out var runningRunId)) {
            return McpResponse.Fail(request.Id, McpErrorCodes.NotSupported,
                $"A test run is already in progress (runId: {runningRunId}).");
        }

        var parseModeResult = ParseTestMode(request.GetParam<string>("testMode"), out var testMode, out var modeError);
        if (!parseModeResult) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, modeError ?? "Invalid testMode.");
        }

        var parseTargetPlatformResult = ParseBuildTarget(request.GetParam<string>("targetPlatform"), out var targetPlatform, out var platformError);
        if (!parseTargetPlatformResult) {
            return McpResponse.Fail(request.Id, McpErrorCodes.InvalidParams, platformError ?? "Invalid targetPlatform.");
        }

        var filter = new Filter {
            testMode = testMode,
            assemblyNames = request.GetParam<string[]>("assemblyNames") ?? [],
            testNames = request.GetParam<string[]>("testNames") ?? [],
            categoryNames = request.GetParam<string[]>("categoryNames") ?? [],
            groupNames = request.GetParam<string[]>("groupNames") ?? [],
            targetPlatform = targetPlatform,
        };

        var runId = Guid.NewGuid().ToString("N");
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        var callback = ScriptableObject.CreateInstance<McpTestRunCallback>();
        callback.Initialize(runId);

        try {
            api.UnregisterCallbacks(callback);
            api.RegisterCallbacks(callback);

            var executionSettings = new ExecutionSettings {
                filters = [filter],
            };

            var sessionId = api.Execute(executionSettings);
            McpTestRunStore.StartRun(runId, sessionId, filter, api, callback);

            return McpResponse.Ok(request.Id, new {
                runId,
                sessionId,
                status = "running",
                filter = McpTestRunStore.SerializeFilter(filter),
            });
        } catch (Exception ex) {
            try {
                api.UnregisterCallbacks(callback);
            } catch {
                // ignored
            }

            Object.DestroyImmediate(callback);
            Object.DestroyImmediate(api);

            return McpResponse.Fail(request.Id, McpErrorCodes.ExecutionFailed,
                $"Failed to start test run: {ex.Message}",
                new { exception = ex.GetType().FullName, stackTrace = ex.StackTrace });
        }
    }

    private static bool ParseTestMode(string? rawMode, out TestMode mode, out string? error) {
        if (string.IsNullOrWhiteSpace(rawMode)) {
            mode = TestMode.EditMode;
            error = null;
            return true;
        }

        if (Enum.TryParse(rawMode, true, out mode)) {
            error = null;
            return true;
        }

        if (string.Equals(rawMode, "edit", StringComparison.OrdinalIgnoreCase)) {
            mode = TestMode.EditMode;
            error = null;
            return true;
        }

        if (string.Equals(rawMode, "play", StringComparison.OrdinalIgnoreCase)) {
            mode = TestMode.PlayMode;
            error = null;
            return true;
        }

        error = $"Invalid testMode '{rawMode}'. Valid values: EditMode, PlayMode.";
        return false;
    }

    private static bool ParseBuildTarget(string? rawTarget, out BuildTarget? target, out string? error) {
        if (string.IsNullOrWhiteSpace(rawTarget)) {
            target = null;
            error = null;
            return true;
        }

        if (Enum.TryParse<BuildTarget>(rawTarget, true, out var parsed)) {
            target = parsed;
            error = null;
            return true;
        }

        error = $"Invalid targetPlatform '{rawTarget}'. Expected UnityEditor.BuildTarget enum name.";
        target = null;
        return false;
    }
}
