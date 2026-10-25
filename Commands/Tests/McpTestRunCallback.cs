using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Vecerdi.UnityMcp.Commands;

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
