using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Unity.Scripting.LifecycleManagement;
using UnityEditor.TestTools.TestRunner.Api;
using Object = UnityEngine.Object;

namespace Vecerdi.UnityMcp.Commands;

[NoAutoStaticsCleanup]
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

    public static bool TryGetRunningRun([NotNullWhen(true)] out string? runId) {
        lock (s_Lock) {
            var running = s_Runs.Values.FirstOrDefault(r => string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase));
            runId = running?.RunId;
            return runId is not null;
        }
    }

    public static bool TryGetLatestRunId([NotNullWhen(true)] out string? runId) {
        lock (s_Lock) {
            runId = s_LatestRunId;
            return !string.IsNullOrWhiteSpace(runId);
        }
    }

    public static bool TryGetSnapshot(string runId, [NotNullWhen(true)] out object? snapshot) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var state)) {
                snapshot = null;
                return false;
            }

            snapshot = state.ToSnapshot();
            return true;
        }
    }

    public static bool TryGetRunForCancellation(string runId, [NotNullWhen(true)] out TestRunState? runState) {
        lock (s_Lock) {
            if (s_Runs.TryGetValue(runId, out runState) && string.Equals(runState.Status, "running", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            runState = null;
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
