using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

internal sealed class McpTestRunCallback : ScriptableObject, IErrorCallbacks {
    private string m_RunId = string.Empty;

    public void Initialize(string runId) {
        m_RunId = runId;
    }

    public void RunStarted(ITestAdaptor testsToRun) {
        McpTestRunStore.OnRunStarted(m_RunId, testsToRun);
    }

    public void RunFinished(ITestResultAdaptor result) {
        McpTestRunStore.OnRunFinished(m_RunId, result);
        McpTestRunStore.CleanupCallback(m_RunId);
    }

    public void TestStarted(ITestAdaptor test) {
        if (!test.IsSuite) {
            McpTestRunStore.OnTestStarted(m_RunId, test);
        }
    }

    public void TestFinished(ITestResultAdaptor result) {
        if (!result.Test.IsSuite) {
            McpTestRunStore.OnTestFinished(m_RunId, result);
        }
    }

    public void OnError(string message) {
        McpTestRunStore.OnRunError(m_RunId, message);
        McpTestRunStore.CleanupCallback(m_RunId);
    }
}

internal static class McpTestRunStore {
    private static readonly object s_Lock = new();
    private static readonly Dictionary<string, TestRunState> s_Runs = new(StringComparer.OrdinalIgnoreCase);
    private static string? s_LatestRunId;

    public static void StartRun(string runId, string sessionId, Filter filter, TestRunnerApi api, McpTestRunCallback callback) {
        lock (s_Lock) {
            s_Runs[runId] = new TestRunState {
                RunId = runId,
                SessionId = sessionId,
                Api = api,
                Callback = callback,
                Status = "running",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Filter = filter,
            };
            s_LatestRunId = runId;
        }
    }

    public static bool TryGetRunningRun(out string? runId) {
        lock (s_Lock) {
            var running = s_Runs.Values.FirstOrDefault(r => string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));
            runId = running?.RunId;
            return runId is not null;
        }
    }

    public static bool TryGetLatestRunId(out string? runId) {
        lock (s_Lock) {
            runId = s_LatestRunId;
            return !string.IsNullOrWhiteSpace(runId);
        }
    }

    public static bool TryGetSnapshot(string runId, out object snapshot) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) {
                snapshot = null!;
                return false;
            }

            snapshot = state.ToSnapshot();
            return true;
        }
    }

    public static bool TryGetRunForCancellation(string runId, out TestRunState runState) {
        lock (s_Lock) {
            if (s_Runs.TryGetValue(runId, out runState!)
             && string.Equals(runState.Status, "running", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            runState = null!;
            return false;
        }
    }

    public static void MarkCancelRequested(string runId) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var runState)) return;
            runState.CancelRequested = true;
        }
    }

    public static object SerializeFilter(Filter filter) => new {
        testMode = filter.testMode.ToString(),
        assemblyNames = filter.assemblyNames ?? [],
        testNames = filter.testNames ?? [],
        categoryNames = filter.categoryNames ?? [],
        groupNames = filter.groupNames ?? [],
        targetPlatform = filter.targetPlatform?.ToString(),
    };

    public static void OnRunStarted(string runId, ITestAdaptor testsToRun) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;
            state.Status = "running";
            state.StartedAtUtc ??= DateTimeOffset.UtcNow;
            state.TotalDiscoveredTests = SafeTestCaseCount(testsToRun);
        }
    }

    public static void OnTestStarted(string runId, ITestAdaptor test) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;
            state.ExecutedTests++;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            state.LastStartedTest = test.FullName;
        }
    }

    public static void OnTestFinished(string runId, ITestResultAdaptor result) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;

            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            var statusText = result.TestStatus.ToString();

            switch (statusText.ToLowerInvariant()) {
                case "passed":
                    state.Passed++;
                    break;
                case "failed":
                    state.Failed++;
                    state.FailedTests.Add(new TestFailure {
                        Name = result.Test.FullName,
                        Message = result.Message,
                        StackTrace = result.StackTrace,
                        Output = result.Output,
                        DurationMs = Math.Max(0, (result.EndTime - result.StartTime).TotalMilliseconds),
                    });
                    break;
                case "skipped":
                case "ignored":
                    state.Skipped++;
                    break;
                case "inconclusive":
                    state.Inconclusive++;
                    break;
                default:
                    state.Other++;
                    break;
            }
        }
    }

    public static void OnRunFinished(string runId, ITestResultAdaptor result) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;

            state.FinishedAtUtc = DateTimeOffset.UtcNow;
            state.LastUpdatedUtc = state.FinishedAtUtc.Value;

            if (state.CancelRequested) {
                state.Status = "cancelled";
            } else {
                state.Status = state.Failed > 0 ? "failed" : "passed";
            }

            if (!string.IsNullOrWhiteSpace(result.Message)) {
                state.RunMessage = result.Message;
            }
        }
    }

    public static void OnRunError(string runId, string message) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;

            state.Status = "error";
            state.RunMessage = message;
            state.FinishedAtUtc = DateTimeOffset.UtcNow;
            state.LastUpdatedUtc = state.FinishedAtUtc.Value;
        }
    }

    public static void CleanupCallback(string runId) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) return;

            try {
                state.Api?.UnregisterCallbacks(state.Callback);
            } catch {
                // ignored
            }

            if (state.Callback is not null) {
                Object.DestroyImmediate(state.Callback);
                state.Callback = null;
            }

            if (state.Api is not null) {
                Object.DestroyImmediate(state.Api);
                state.Api = null;
            }
        }
    }

    private static int SafeTestCaseCount(ITestAdaptor testsToRun) {
        try {
            return testsToRun.TestCaseCount;
        } catch {
            return 0;
        }
    }

    internal sealed class TestRunState {
        public required string RunId { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public TestRunnerApi? Api { get; set; }
        public McpTestRunCallback? Callback { get; set; }
        public string Status { get; set; } = "running";
        public bool CancelRequested { get; set; }
        public Filter? Filter { get; set; }
        public int TotalDiscoveredTests { get; set; }
        public int ExecutedTests { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Inconclusive { get; set; }
        public int Other { get; set; }
        public string? LastStartedTest { get; set; }
        public string? RunMessage { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public List<TestFailure> FailedTests { get; } = [];

        public object ToSnapshot() {
            var failures = FailedTests
                .Take(200)
                .Select(f => new {
                    name = f.Name,
                    message = f.Message,
                    stackTrace = f.StackTrace,
                    output = f.Output,
                    durationMs = f.DurationMs,
                })
                .ToList();

            return new {
                runId = RunId,
                sessionId = SessionId,
                status = Status,
                cancelRequested = CancelRequested,
                filter = Filter is null ? null : SerializeFilter(Filter),
                totals = new {
                    discovered = TotalDiscoveredTests,
                    executed = ExecutedTests,
                    passed = Passed,
                    failed = Failed,
                    skipped = Skipped,
                    inconclusive = Inconclusive,
                    other = Other,
                },
                lastStartedTest = LastStartedTest,
                message = RunMessage,
                createdAtUtc = CreatedAtUtc,
                startedAtUtc = StartedAtUtc,
                lastUpdatedAtUtc = LastUpdatedUtc,
                finishedAtUtc = FinishedAtUtc,
                failures,
                failureCount = FailedTests.Count,
                failuresTruncated = FailedTests.Count > failures.Count,
            };
        }
    }

    internal sealed class TestFailure {
        public string Name { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string? StackTrace { get; set; }
        public string? Output { get; set; }
        public double DurationMs { get; set; }
    }
}
