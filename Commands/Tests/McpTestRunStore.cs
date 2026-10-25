using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
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

    public static bool TryGetRunSummary(string runId, [NotNullWhen(true)] out string? status, [NotNullWhen(true)] out string? summary) {
        lock (s_Lock) {
            if (!s_Runs.TryGetValue(runId, out var testRunState)) {
                status = null;
                summary = null;
                return false;
            }

            status = testRunState.Status;
            summary = testRunState.GetSummary();
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

        public string GetSummary() {
            var sb = new StringBuilder();

            // 'ran' (executed) is the count the filter actually matched and ran; 'discovered' is the ENTIRE test
            // tree in the editor (all modes), which is NOT the filter-matched count - keeping them as separate
            // labelled numbers stops "5 passed" from being mistaken for "the filter matched exactly those 5".
            var summaryLine = Failed > 0
                ? $"Unity tests {Status.ToUpperInvariant()}: {Failed} failed, {Passed} passed of {ExecutedTests} ran"
                : $"Unity tests {Status.ToUpperInvariant()}: {Passed} passed of {ExecutedTests} ran";

            if (Skipped > 0 || Inconclusive > 0 || Other > 0) {
                summaryLine += $" | skipped {Skipped}, inconclusive {Inconclusive}, other {Other}";
            }

            sb.AppendLine(summaryLine);
            sb.AppendLine($"status: {Status}");
            sb.AppendLine($"matched & ran: {ExecutedTests}, passed: {Passed}, failed: {Failed}, skipped: {Skipped}, inconclusive: {Inconclusive}, other: {Other}");
            sb.AppendLine($"discovered (entire test tree, all modes - NOT the filter-matched count): {TotalDiscoveredTests}");

            sb.AppendLine($"resolved filter: {DescribeResolvedFilter(Filter)}");
            if (ExecutedTests == 0 && !string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase)) {
                sb.AppendLine("WARNING: the filter matched 0 tests - check the resolved filter above (wrong assembly/test name or mode?).");
            }

            sb.AppendLine($"runId: {RunId}");

            if (!string.IsNullOrWhiteSpace(RunMessage)) {
                sb.AppendLine();
                sb.AppendLine($"message: {RunMessage}");
            }

            if (FailedTests.Count > 0) {
                const int maxFailuresToShow = 25;
                sb.AppendLine();
                sb.AppendLine($"failures{(FailedTests.Count > maxFailuresToShow ? $" (showing first {maxFailuresToShow} of {FailedTests.Count})" : "")}:");

                foreach (var failure in FailedTests.Take(maxFailuresToShow)) {
                    sb.AppendLine($"- {failure.Name}");
                    if (!string.IsNullOrWhiteSpace(failure.Message)) {
                        sb.AppendLine($"  message:\n{failure.Message}");
                        sb.AppendLine();
                    }

                    if (!string.IsNullOrWhiteSpace(failure.StackTrace)) {
                        sb.AppendLine($"  stack: {failure.StackTrace}");
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }

        private static string DescribeResolvedFilter(Filter? filter) {
            if (filter is null) {
                return "(none reported)";
            }

            var parts = new List<string> {
                $"testMode={filter.testMode}",
            };

            var scopingParts = new List<string>();
            AppendArray("assemblyNames", filter.assemblyNames, scopingParts);
            AppendArray("testNames", filter.testNames, scopingParts);
            AppendArray("categoryNames", filter.categoryNames, scopingParts);
            AppendArray("groupNames", filter.groupNames, scopingParts);

            if (filter.targetPlatform is { } targetPlatform) {
                scopingParts.Add($"targetPlatform={targetPlatform}");
            }

            parts.AddRange(scopingParts);
            if (scopingParts.Count == 0) {
                parts.Add("no name/assembly/category/group filter - runs everything in this mode");
            }

            return string.Join(", ", parts);

            static void AppendArray(string name, string[]? values, List<string> parts) {
                if (values is { Length: > 0 }) {
                    parts.Add($"{name}=[{string.Join(", ", values)}]");
                }
            }
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
