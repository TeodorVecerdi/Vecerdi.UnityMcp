namespace UnityMcp;

/// <summary>
/// Opens the pooled connection to an editor, treating a domain reload as something to wait out rather than
/// an error. The editor keeps its discovery entry across a reload and flags it <c>reloading</c>, so a failed
/// connect can be classified: the editor is gone (no entry), it is reloading (wait, then connect), or its MCP
/// server is genuinely unreachable. Nothing has been sent when the wait happens, so waiting is safe for every
/// tool, idempotent or not.
/// </summary>
public static class EditorAvailability {
    /// <summary>How long a caller is willing to wait for a reloading editor to come back before failing.</summary>
    public static TimeSpan ReloadWait { get; set; } = TimeSpan.FromSeconds(60);

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
        Exception firstFailure;
        try {
            return (await pool.AcquireAsync(port, ct), null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            firstFailure = ex;
        }

        var editor = FindEditor(port);
        if (editor is null) {
            return (null, $"No Unity Editor is registered on port {port} - it has probably been closed. Use 'list_editors' to see the running editors.");
        }

        if (!editor.IsReloading) {
            // A compile that just finished cleanly flips to 'reloading' a moment after the socket drops. Give the
            // registry one poll to catch up before calling the editor unreachable.
            await Task.Delay(PollInterval, ct);
            editor = FindEditor(port);
            if (editor is null) {
                return (null, $"No Unity Editor is registered on port {port} - it has probably been closed. Use 'list_editors' to see the running editors.");
            }

            if (!editor.IsReloading) {
                return (null,
                    $"Failed to connect to Unity Editor '{editor.ProjectName}' on port {port}: {firstFailure.Message}\n\n" +
                    $"The editor process is alive (PID {editor.ProcessId}, state '{editor.State}') but its MCP server is not accepting " +
                    "connections. Make sure the MCP plugin is active in that editor.");
            }
        }

        var deadline = DateTime.UtcNow + ReloadWait;
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct);

            editor = FindEditor(port);
            if (editor is null) {
                return (null, $"Unity Editor on port {port} was closed while reloading the script domain. Use 'list_editors' to see the running editors.");
            }

            if (editor.IsReloading) {
                continue;
            }

            try {
                return (await pool.AcquireAsync(port, ct), null);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // The entry flipped back to ready a beat before the server started listening; keep polling.
            }
        }

        var waited = editor.StateChangedAt is { } since ? (DateTime.UtcNow - since.UtcDateTime).TotalSeconds : ReloadWait.TotalSeconds;
        return (null,
            $"Unity Editor '{editor.ProjectName}' (port {port}) has been reloading the script domain for {waited:F0}s and its MCP server is not back yet. " +
            "It may be stuck behind a modal dialog or a long import - check the editor window, then retry.");
    }
}
