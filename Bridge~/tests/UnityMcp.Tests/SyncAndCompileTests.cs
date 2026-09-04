using ModelContextProtocol.Protocol;
using UnityMcp;
using Xunit;

namespace UnityMcp.Tests;

/// <summary>
/// The join-an-in-flight-compile path of <c>sync_and_compile</c>: when the editor advertises a compile it is
/// already in, the tool rides it instead of forcing a second one, and its diagnostics marker is that compile's
/// own start time rather than the moment the call arrived.
/// </summary>
public sealed class SyncAndCompileTests {
    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static EditorInstance Entry(string state, DateTimeOffset? compileStartedAt) => new() {
        Port = 9100,
        ProjectName = "MyGame",
        State = state,
        StateChangedAt = DateTimeOffset.UtcNow,
        CompilationStartedAt = compileStartedAt,
    };

    /// <summary>A scripted editor that records every command it is sent.</summary>
    private static (ScriptedUnityConnection Connection, List<string> Commands) Editor(Func<string, UnityResponse> responder) {
        var commands = new List<string>();
        var connection = new ScriptedUnityConnection((command, _) => {
            commands.Add(command);
            return responder(command);
        });
        return (connection, commands);
    }

    private static UnityResponse Ok() => new() { Id = "1", Success = true };

    [Theory]
    [InlineData(EditorInstanceState.Compiling, true, true)]
    [InlineData(EditorInstanceState.Reloading, true, true)]
    [InlineData(EditorInstanceState.Ready, true, false)]       // a finished compile is not in flight
    [InlineData(EditorInstanceState.Compiling, false, false)]  // compiling but start unknown: cannot place the marker
    [InlineData(EditorInstanceState.Reloading, false, false)]  // a reload no compile caused
    public void DetectInFlightCompile_RequiresBusyStateAndKnownStart(string state, bool hasStart, bool expectInFlight) {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        var entry = Entry(state, hasStart ? startedAt : null);

        var detected = UnityTools.DetectInFlightCompile(entry);

        Assert.Equal(expectInFlight ? startedAt : null, detected);
    }

    [Fact]
    public void DetectInFlightCompile_NoEntry_IsNull() {
        Assert.Null(UnityTools.DetectInFlightCompile(null));
    }

    [Fact]
    public async Task InFlightCompile_NothingNewer_SkipsRecompileAndReportsItsErrors() {
        var compileStartedAt = DateTimeOffset.UtcNow.AddSeconds(-4);
        var (editor, commands) = Editor(command => command switch {
            "unity.editor.getCompilationStatus" => ScriptedUnityConnection.CompilationStatusResponse(isCompiling: false, isUpdating: false),
            "unity.debug.getLogs" => ScriptedUnityConnection.LogsResponse(
                ("error", "NullReferenceException from an earlier play session", compileStartedAt.AddSeconds(-30)),
                // Logged per assembly while the compile was still running - before this call arrived.
                ("error", "Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist", compileStartedAt.AddSeconds(2))),
            _ => Ok(),
        });

        var result = await UnityTools.RunSyncAndCompileAsync(editor, Entry(EditorInstanceState.Compiling, compileStartedAt), () => null, CancellationToken.None);

        var text = TextOf(result);
        Assert.True(result.IsError);
        Assert.Contains("Joined a compile that was already running", text);
        Assert.Contains("no second compile was needed", text);
        Assert.Contains("Compilation FAILED with 1 new error", text);
        Assert.Contains("CS0103", text);
        Assert.DoesNotContain("NullReferenceException", text);
        Assert.Contains("unity.editor.refreshAssets", commands);
        Assert.DoesNotContain("unity.editor.recompile", commands);
    }

    [Fact]
    public async Task InFlightCompile_RefreshFindsNewerEdits_WaitsForTheSecondCompileToo() {
        var compileStartedAt = DateTimeOffset.UtcNow.AddSeconds(-4);
        var refreshed = false;
        var statusPollsAfterRefresh = 0;
        var (editor, commands) = Editor(command => {
            switch (command) {
                case "unity.editor.refreshAssets":
                    refreshed = true;
                    return Ok();
                case "unity.editor.getCompilationStatus":
                    // Idle before the refresh; busy for the first poll after it, then idle again.
                    var busy = refreshed && ++statusPollsAfterRefresh <= 1;
                    return ScriptedUnityConnection.CompilationStatusResponse(isCompiling: busy, isUpdating: false);
                case "unity.debug.getLogs":
                    return ScriptedUnityConnection.LogsResponse();
                default:
                    return Ok();
            }
        });

        var result = await UnityTools.RunSyncAndCompileAsync(editor, Entry(EditorInstanceState.Reloading, compileStartedAt), () => null, CancellationToken.None);

        var text = TextOf(result);
        Assert.False(result.IsError ?? false);
        Assert.Contains("newer edits triggered a further compile", text);
        Assert.Contains("no errors", text);
        Assert.DoesNotContain("unity.editor.recompile", commands);
        Assert.True(statusPollsAfterRefresh >= 2, "should have waited for the refresh-triggered compile to settle");
    }

    [Fact]
    public async Task NoInFlightCompile_ForcesRecompileAsBefore() {
        var (editor, commands) = Editor(command => command switch {
            "unity.editor.getCompilationStatus" => ScriptedUnityConnection.CompilationStatusResponse(isCompiling: false, isUpdating: false),
            "unity.debug.getLogs" => ScriptedUnityConnection.LogsResponse(),
            _ => Ok(),
        });

        var stale = Entry(EditorInstanceState.Ready, DateTimeOffset.UtcNow.AddMinutes(-10));
        var result = await UnityTools.RunSyncAndCompileAsync(editor, stale, () => stale, CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Contains("unity.editor.recompile", commands);
        Assert.DoesNotContain("Joined", TextOf(result));
    }

    [Fact]
    public async Task CompileThatStartsDuringTheDrain_IsJoinedToo() {
        // Idle at arrival; a compile the user triggered starts and finishes while we drain, leaving its start on record.
        var statusPolls = 0;
        var (editor, commands) = Editor(command => command switch {
            "unity.editor.getCompilationStatus" => ScriptedUnityConnection.CompilationStatusResponse(isCompiling: ++statusPolls <= 1, isUpdating: false),
            "unity.debug.getLogs" => ScriptedUnityConnection.LogsResponse(),
            _ => Ok(),
        });

        var result = await UnityTools.RunSyncAndCompileAsync(
            editor,
            Entry(EditorInstanceState.Ready, null),
            () => Entry(EditorInstanceState.Ready, DateTimeOffset.UtcNow.AddSeconds(1)),
            CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Contains("Joined a compile that was already running", TextOf(result));
        Assert.DoesNotContain("unity.editor.recompile", commands);
    }
}
