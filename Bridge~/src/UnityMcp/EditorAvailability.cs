namespace UnityMcp;

/// <summary>
/// Opens the pooled connection to an editor, treating a domain reload as something to wait out rather than
/// an error. The editor keeps its discovery entry across a reload and flags it <c>reloading</c>, so a failed
/// connect can be classified: the editor is gone (no entry), it is reloading or about to (wait, then connect),
/// or its MCP server is genuinely unreachable. Nothing has been sent when the wait happens, so waiting is safe
/// for every tool, idempotent or not.
/// </summary>
public static class EditorAvailability {
    /// <summary>How long a caller is willing to wait for a reloading editor to come back before failing.</summary>
    public static TimeSpan ReloadWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the entry may read <c>ready</c> while the connect keeps failing before the editor is declared
    /// unreachable. Covers the moments around a reload where the state and the socket are not yet in step: the
    /// socket drops a beat before the entry flips to <c>reloading</c>, and the entry is rewritten a beat before
    /// the listener is back.
    /// </summary>
    public static TimeSpan ReadyGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The registry read used to classify a failed connect. Replaceable so the wait logic can be tested without a
    /// discovery file.
    /// </summary>
    internal static Func<int, EditorInstance?> FindEditor { get; set; } = EditorDiscovery.FindEditorByPort;

    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Acquire an open connection to the editor on <paramref name="port"/>, waiting through a domain reload if
    /// the registry says one is in progress. Returns either a connection or an agent-facing error message.
    /// </summary>
    public static async Task<(IUnityConnection? Connection, string? Error)> AcquireAsync(UnityConnectionPool pool, int port, CancellationToken ct) {
        Exception lastFailure;
        try {
            return (await pool.AcquireAsync(port, ct), null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            lastFailure = ex;
        }

        var deadline = DateTime.UtcNow + ReloadWait;
        DateTime? readySince = null;
        var sawReload = false;
        EditorInstance? editor;

        while (true) {
            editor = FindEditor(port);
            if (editor is null) {
                return (null, sawReload
                    ? $"Unity Editor on port {port} was closed while reloading the script domain. Use 'list_editors' to see the running editors."
                    : $"No Unity Editor is registered on port {port} - it has probably been closed. Use 'list_editors' to see the running editors.");
            }

            if (editor.IsReloading || editor.IsCompiling) {
                // Reloading, or compiling with a reload imminent. Keep waiting; the connect is retried each poll.
                sawReload = true;
                readySince = null;
            } else {
                readySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - readySince.Value >= ReadyGrace) {
                    return (null,
                        $"Failed to connect to Unity Editor '{editor.ProjectName}' on port {port}: {lastFailure.Message}\n\n" +
                        $"The editor process is alive (PID {editor.ProcessId}, state '{editor.State}') but its MCP server has not accepted " +
                        $"a connection for {ReadyGrace.TotalSeconds:F0}s. Make sure the MCP plugin is active in that editor.");
                }
            }

            if (DateTime.UtcNow >= deadline) {
                break;
            }

            await Task.Delay(PollInterval, ct);

            try {
                return (await pool.AcquireAsync(port, ct), null);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                lastFailure = ex;
            }
        }

        var waited = editor.StateChangedAt is { } since ? (DateTime.UtcNow - since.UtcDateTime).TotalSeconds : ReloadWait.TotalSeconds;
        return (null,
            $"Unity Editor '{editor.ProjectName}' (port {port}) has been {editor.State} for {waited:F0}s and its MCP server is not back yet. " +
            "It may be stuck behind a modal dialog or a long import - check the editor window, then retry.");
    }
}
